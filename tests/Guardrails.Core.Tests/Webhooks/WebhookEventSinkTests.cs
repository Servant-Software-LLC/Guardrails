using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Webhooks;

/// <summary>
/// Pins the webhook dispatcher (#585 layer 3 — docs/plans/585-layer3-webhooks-contract.md): the
/// §5.2 bounds, the §5.3 circuit, the §3.2 queue's drop accounting, the §3.3 six-step teardown, the
/// §6.5 production construction path, and the §5.4/§6.6 rule that no notice ever prints a credential
/// or a URL path.
///
/// <para>Authored RED against <see cref="WebhookEventSink"/>'s throwing member stubs (task 06); task
/// 07 implements the queue, the pump, the circuit and disposal. Every test here calls at least one
/// stubbed member (the internal constructor, <see cref="WebhookEventSink.Emit"/>,
/// <see cref="WebhookEventSink.DisposeAsync"/>, or <see cref="WebhookEventSink.TryStart"/>), so all
/// sixteen fail on <c>NotImplementedException</c> rather than passing vacuously against a constant
/// that task 04/05 already landed.</para>
/// </summary>
public sealed class WebhookEventSinkTests
{
    private static readonly Uri DefaultUrl = new("https://hooks.example.test/hook");

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test doubles
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A scriptable <see cref="HttpMessageHandler"/> that records every request it receives — the
    /// delivery id (from the <c>X-Guardrails-Delivery-Id</c> header, §4.3), the time it arrived, and
    /// how many times each id has been seen. Almost every assertion below is a statement about that
    /// record.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(string DeliveryId, DateTimeOffset At)> _requests = [];

        public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>>? OnRequest { get; set; }

        public IReadOnlyList<(string DeliveryId, DateTimeOffset At)> Requests
        {
            get { lock (_gate) return [.. _requests]; }
        }

        public int CountFor(string deliveryId)
        {
            lock (_gate) return _requests.Count(r => r.DeliveryId == deliveryId);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string id = DeliveryIdOf(request);
            int attempt;
            lock (_gate)
            {
                _requests.Add((id, DateTimeOffset.UtcNow));
                attempt = _requests.Count(r => r.DeliveryId == id);
            }

            if (OnRequest is not null)
                return await OnRequest(request, attempt, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>A transport that fails on every send AND on its own disposal (behaviour 8).</summary>
    private sealed class ThrowingDisposeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new IOException("transport exploded mid-send");

        protected override void Dispose(bool disposing)
            => throw new IOException("transport exploded on dispose");
    }

    /// <summary>Wraps a stream and counts bytes actually pulled out of it (behaviour 16).</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int n = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            BytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A response body that FAULTS the moment it is read — the shape of a receiver that answers 200 and
    /// closes without the body its <c>Content-Length</c> declared, a tunnel (ngrok, cloudflared)
    /// resetting, or a proxy truncating. Reading it is the only thing that fails; the status line has
    /// already been received.
    /// </summary>
    private sealed class FaultingStream : Stream
    {
        private static IOException Fault() => new("connection reset while reading the response body");

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw Fault();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException<int>(Fault());

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            => ValueTask.FromException<int>(Fault());

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static EventDelivery Row(string id, string kind = "note") =>
        new(id, kind, $"{{\"kind\":\"{kind}\",\"id\":\"{id}\"}}");

    private static string DeliveryIdOf(HttpRequestMessage request) =>
        request.Headers.TryGetValues("X-Guardrails-Delivery-Id", out var values) ? values.First() : "<missing>";

    private static int ParseDroppedCount(IEnumerable<string> notices)
    {
        foreach (string notice in notices)
        {
            Match match = Regex.Match(notice, @"(\d+) dropped");
            if (match.Success)
                return int.Parse(match.Groups[1].Value);
        }

        throw new InvalidOperationException("no end-of-run summary notice was found among: " + string.Join(" | ", notices));
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, or throws with <paramref name="what"/> in the
    /// message. Every wait in this file is on an OBSERVABLE, never a sleep: the budgets here are
    /// sub-second at the time scales used, so a fixed delay is a coin flip under parallel test load.
    /// </summary>
    private static async Task WaitFor(Func<bool> condition, string what, int timeoutSeconds = 10)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.True(condition(), $"timed out waiting for: {what}");
    }

    /// <summary>
    /// Asserts <paramref name="actual"/> falls inside <paramref name="scaledStep"/>'s jittered band
    /// (§5.2: <c>[0.5, 1.5)</c>), plus generous slack for timer resolution and scheduler noise —
    /// Windows' own timer granularity alone is ~15 ms.
    /// </summary>
    private static void AssertWithinJitteredBand(TimeSpan actual, TimeSpan scaledStep, TimeSpan slack)
    {
        (TimeSpan lower, TimeSpan upper) = JitteredBand(scaledStep, slack);
        Assert.InRange(actual, lower, upper);
    }

    /// <summary>The same band as a predicate, for deciding whether a measurement is worth re-taking.</summary>
    private static bool InJitteredBand(TimeSpan actual, TimeSpan scaledStep, TimeSpan slack)
    {
        (TimeSpan lower, TimeSpan upper) = JitteredBand(scaledStep, slack);
        return actual >= lower && actual <= upper;
    }

    private static (TimeSpan Lower, TimeSpan Upper) JitteredBand(TimeSpan scaledStep, TimeSpan slack)
    {
        TimeSpan lower = scaledStep * WebhookEventSink.JitterLowerBound - slack;
        if (lower < TimeSpan.Zero)
            lower = TimeSpan.Zero;
        return (lower, scaledStep * WebhookEventSink.JitterUpperBound + slack);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The bounds (§5.2)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task BackoffScheduleIsOneTwoFourWithJitter()
    {
        // Constants.
        Assert.Equal(4, WebhookEventSink.MaxAttemptsPerRow);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
            WebhookEventSink.BackoffSteps);
        Assert.Equal(0.5, WebhookEventSink.JitterLowerBound);
        Assert.Equal(1.5, WebhookEventSink.JitterUpperBound);

        // Behaviour: one row against a transport that returns 503 every time produces exactly 4
        // requests, and the three gaps between them each fall inside their jittered band.
        const double scale = 0.2; // 1/2/4s steps -> 200/400/800ms; keeps the test fast but measurable
        // ASSERT THE COMPUTATION, NOT THE DELIVERY. A wall-clock gap between two recorded requests is
        // the delay the sink COMPUTED plus however long the thread pool took to run the timer's
        // continuation, and only the first of those is under test. When the pool is saturated the second
        // is neither small nor bounded: it is governed by the runtime's thread-INJECTION rate (~1 per
        // 500 ms once starved), which quantizes an observed gap near a second whatever the sink asked
        // for.
        //
        // MEASURED, twice, on exactly this test. Alone it passes in ~1s. Inside the full suite it failed
        // reproducibly with gap1 at 926 ms and 982 ms against a 450 ms band top — and widening the slack
        // to cover that would have swallowed the band whole (1300 ms of tolerance on a 200 ms step
        // admits a schedule with no backoff at all). A take-the-best-of-three mitigation was tried next
        // and was still not enough: the v1.17.0 ubuntu release run starved all three samples under the
        // concurrent whole-solution profile (#566).
        //
        // So stop sampling the delivery. `ComputedBackoffs` is what the sink decided, which is the thing
        // this test names, and it is unaffected by how busy the machine is. The bands below are the same
        // bands — what changed is that they are now EXACT, with zero slack, because a computed value has
        // no scheduling noise in it to tolerate. A wrong schedule is a wrong computation and still fails.
        {
            var notices = new List<string>();
            var handler = new RecordingHandler
            {
                OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            };

            using var cts = new CancellationTokenSource();
            var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, scale, cts.Token);

            sink.Emit(Row("row-1"));

            // WAIT FOR THE SCHEDULE, do not race the teardown. Disposing immediately makes this test a
            // coin flip on jitter even on an idle machine: the jittered backoff can reach
            // (1+2+4) * 1.5 * 0.2 = 2.1s, while DisposeAsync's backlog budget is 10s * 0.2 = 2.0s - and
            // the backlog phase abandons retries (one attempt per row), so the fourth request is simply
            // never made. Observed as "Expected: 4 / Actual: 3". Poll for the schedule, then tear down.
            await WaitFor(() => handler.Requests.Count >= 4, "the row's full four-attempt schedule", timeoutSeconds: 30);

            await sink.DisposeAsync();

            IReadOnlyList<(string DeliveryId, DateTimeOffset At)> requests = handler.Requests;
            Assert.Equal(4, requests.Count);

            // Four attempts means exactly three waits between them — a fourth computed backoff would mean
            // the sink intended a fifth attempt it never declared, which MaxAttemptsPerRow forbids.
            IReadOnlyList<TimeSpan> backoffs = sink.ComputedBackoffs;
            Assert.Equal(3, backoffs.Count);

            AssertWithinJitteredBand(backoffs[0], WebhookEventSink.BackoffSteps[0] * scale, TimeSpan.Zero);
            AssertWithinJitteredBand(backoffs[1], WebhookEventSink.BackoffSteps[1] * scale, TimeSpan.Zero);
            AssertWithinJitteredBand(backoffs[2], WebhookEventSink.BackoffSteps[2] * scale, TimeSpan.Zero);

            // The one comparison that is exact regardless of the jitter draw, since 4s x 0.5 always
            // exceeds 1s x 1.5: jitter exists so a parallel burst of failures does not resynchronize,
            // not to blur the shape of the schedule.
            Assert.True(backoffs[2] > backoffs[0]);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task PerRowCeilingIsFortyFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), WebhookEventSink.PerRowCeiling);

        // Behaviour: the ceiling is a per-row CancellationTokenSource, so it truncates the schedule
        // however the attempt timings fall. With a transport that hangs past the per-attempt timeout
        // on every attempt, the full schedule (4 x PerAttemptTimeout + 1+2+4 backoff, ~47s unscaled)
        // exceeds the 45s ceiling, so the row is cut off AT the ceiling rather than running the
        // schedule out.
        //
        // Elapsed wall-clock time — not the attempt count — is the deterministic signal here: because
        // 4 x PerAttemptTimeout (40s) alone is already within a few seconds of the 45s ceiling, the
        // exact number of requests the fake handler sees before the ceiling fires depends on the
        // random jitter draw. What is NOT random is that the ceiling always cuts elapsed time to
        // ~PerRowCeiling: if the ceiling were not enforced at all, elapsed would land near the full
        // ~47-50s schedule instead, comfortably outside the assertion below.
        //
        // NOTE, since teardown gained `_draining`: this row is emitted and the sink torn down straight
        // away, so the drain's one-attempt-per-row rule (§3.3 step 2) now bounds the same wall clock the
        // ceiling does. Both bounds are real and the assertions below hold under either; what this test
        // still uniquely owns is the CONSTANT above — the ceiling's declared value — and the negative
        // statement that no path here runs the unbounded schedule out. The ceiling's own wall-clock
        // signature (45s vs the schedule's ~47s) is only ~4% wide by the design's own choice of numbers,
        // so it was never the thing this elapsed comparison was discriminating.
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromDays(1), ct); // cut off by the per-attempt timeout
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        const double scale = 0.05; // 45s ceiling -> 2.25s; keeps the test well under a few seconds
        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, scale, cts.Token);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        sink.Emit(Row("row-1"));
        await sink.DisposeAsync();
        stopwatch.Stop();

        TimeSpan scaledCeiling = WebhookEventSink.PerRowCeiling * scale;
        TimeSpan scaledFullSchedule =
            (WebhookEventSink.PerAttemptTimeout * WebhookEventSink.MaxAttemptsPerRow
             + WebhookEventSink.BackoffSteps[0] + WebhookEventSink.BackoffSteps[1] + WebhookEventSink.BackoffSteps[2])
            * scale;

        Assert.True(
            stopwatch.Elapsed <= scaledCeiling + TimeSpan.FromSeconds(2),
            $"row ran past its ceiling: elapsed {stopwatch.Elapsed}, ceiling {scaledCeiling}");
        Assert.True(
            stopwatch.Elapsed < scaledFullSchedule,
            $"row ran the full unbounded schedule out instead of being cut off: elapsed {stopwatch.Elapsed}, full schedule {scaledFullSchedule}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The circuit (§5.3)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task CircuitOpensAtExactlyFiveConsecutiveFailures()
    {
        Assert.Equal(5, WebhookEventSink.CircuitThreshold);

        // "ok*" ids succeed; everything else is a hard, non-retryable failure decided in one attempt,
        // so each row's fate (and the circuit's count) advances quickly.
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond = (req, _, _) =>
        {
            HttpStatusCode status = DeliveryIdOf(req).StartsWith("ok", StringComparison.Ordinal)
                ? HttpStatusCode.OK
                : HttpStatusCode.BadRequest;
            return Task.FromResult(new HttpResponseMessage(status));
        };

        // Scenario A: exactly five consecutive failures opens the circuit — row five is still
        // attempted; row six (after the fifth failure) gets zero requests.
        var noticesA = new List<string>();
        var handlerA = new RecordingHandler { OnRequest = respond };
        using var ctsA = new CancellationTokenSource();
        var sinkA = new WebhookEventSink(DefaultUrl, null, "guardrails/test", noticesA.Add, handlerA, 0.05, ctsA.Token);

        for (int i = 1; i <= 4; i++)
            sinkA.Emit(Row($"fail-{i}"));
        sinkA.Emit(Row("fail-5"));

        // POLL, do not hope. Emit only ENQUEUES; the pump delivers on its own task. "dropped-after-open"
        // is only meaningful once the five consecutive failures have ACTUALLY been attempted and opened
        // the circuit - emitting it immediately races the pump. Measured: this test passes in isolation
        // (~59ms) and FAILED inside the full 2470-test suite (~699ms, 12x slower under parallel load),
        // because at timeScale 0.05 every budget here is sub-second. Waiting on the observable makes the
        // ordering deterministic instead of load-dependent.
        //
        // The observable is now the LATCH ITSELF (CircuitIsOpen), not the arrival of fail-5's request.
        // Request arrival is a PROXY that is true strictly BEFORE the state it stands for: the pump
        // records the request, then awaits the response, then settles the row, then latches. Waiting on
        // the proxy leaves that whole window open.
        await WaitFor(() => sinkA.CircuitIsOpen, "the circuit to open after five consecutive failures");

        sinkA.Emit(Row("dropped-after-open"));
        // TERMINAL-PHASE SENTINEL (design 3.3 step 3): DisposeAsync ALWAYS spends one attempt on the
        // LAST-ENQUEUED row, ignoring the circuit - that is precisely the guarantee
        // TerminalRowIsAttemptedWithTheCircuitOpen pins. Without a trailing row here, the row asserted
        // to get ZERO attempts would itself BE the terminal row and would be attempted exactly once,
        // so the assertion below would contradict the spec rather than test it.
        sinkA.Emit(Row("terminal-sentinel-a"));

        await sinkA.DisposeAsync();

        Assert.True(handlerA.CountFor("fail-5") >= 1, "the fifth failing row must still be attempted — the circuit is closed until AFTER it");
        Assert.Equal(0, handlerA.CountFor("dropped-after-open"));

        // Scenario B: "consecutive" means consecutive. Four failures, then a success (which resets
        // the counter), then four more failures must NOT open the circuit — a naive "five failures
        // ever" counter would fail this half.
        var noticesB = new List<string>();
        var handlerB = new RecordingHandler { OnRequest = respond };
        using var ctsB = new CancellationTokenSource();
        var sinkB = new WebhookEventSink(DefaultUrl, null, "guardrails/test", noticesB.Add, handlerB, 0.05, ctsB.Token);

        for (int i = 1; i <= 4; i++)
            sinkB.Emit(Row($"b1-fail-{i}"));
        sinkB.Emit(Row("ok-reset"));
        for (int i = 1; i <= 4; i++)
            sinkB.Emit(Row($"b2-fail-{i}"));
        sinkB.Emit(Row("still-attempted"));

        await sinkB.DisposeAsync();

        Assert.True(handlerB.CountFor("still-attempted") >= 1, "four failures after a reset must not open the circuit");
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task CircuitNeverCloses()
    {
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        // Open the circuit: five consecutive terminal failures.
        for (int i = 1; i <= 5; i++)
            sink.Emit(Row($"open-{i}"));

        // POLL BEFORE THE FLIP. Emit only enqueues; the pump delivers on its own task, and a blocked
        // channel reader is never resumed synchronously inside TryWrite. Flipping the handler straight
        // after the burst therefore races the pump and the test thread always wins — all five rows are
        // then served OK, the circuit never opens, and this test silently stops testing anything.
        //
        // Wait on the LATCH, not on the arrival of the fifth row's request: the request arrives strictly
        // before the latch it stands for, so the proxy leaves exactly the window this poll exists to
        // close.
        await WaitFor(() => sink.CircuitIsOpen, "the circuit to open after five consecutive failures");

        // Flip the transport to succeed and wait comfortably longer than any plausible cooldown at
        // this time scale — there is no half-open probe and no timer, so nothing should change.
        handler.OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        sink.Emit(Row("after-cooldown-1"));
        sink.Emit(Row("after-cooldown-2"));
        // TERMINAL-PHASE SENTINEL (design 3.3 step 3): DisposeAsync ALWAYS spends one attempt on the
        // LAST-ENQUEUED row, ignoring the circuit - that is precisely the guarantee
        // TerminalRowIsAttemptedWithTheCircuitOpen pins. Without a trailing row here, the row asserted
        // to get ZERO attempts would itself BE the terminal row and would be attempted exactly once,
        // so the assertion below would contradict the spec rather than test it.
        sink.Emit(Row("terminal-sentinel-c"));

        await sink.DisposeAsync();

        Assert.Equal(0, handler.CountFor("after-cooldown-1"));
        Assert.Equal(0, handler.CountFor("after-cooldown-2"));

        // There is no API to reset the circuit: WebhookEventSink's whole surface is TryStart, Emit,
        // DisposeAsync, the internal constructor and the two internal readbacks (the pinned stub
        // shape in WebhookEventSink.cs) — no reset or probe entry point exists to call.
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The queue (§3.2)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task FullQueueDropsTheOldestNotTheNewest()
    {
        Assert.Equal(1024, WebhookEventSink.QueueCapacity);

        var notices = new List<string>();
        var release = new TaskCompletionSource();
        var handler = new RecordingHandler
        {
            OnRequest = async (_, _, ct) =>
            {
                await release.Task.WaitAsync(ct); // blocks the pump until the test releases it
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        // Row 0 is dequeued by the pump and blocks it; everything after fills the queue. WAIT for that
        // to have actually happened - "blocks it immediately" was an assumption, not a fact. Emit only
        // ENQUEUES; if the flood starts before the pump has entered the handler, the pump dequeues
        // row-00001 (and possibly more) as they arrive, so those rows are ATTEMPTED rather than
        // displaced and the DoesNotContain assertions below fail with "Item found in set". Observed on
        // a macOS CI runner, on the same commit that passed macOS in a sibling run - a pure race.
        // RecordingHandler records a request BEFORE invoking OnRequest, so this poll is exactly the
        // signal that the pump is inside the blocking handler.
        sink.Emit(Row("row-00000"));

        DateTime pumpBlockedDeadline = DateTime.UtcNow.AddSeconds(30);
        while (handler.CountFor("row-00000") == 0 && DateTime.UtcNow < pumpBlockedDeadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(handler.CountFor("row-00000") >= 1, "the pump must be blocked on row 0 before the flood starts, or the rows this test expects to be displaced are attempted instead");

        const int excess = 100; // comfortably more than capacity
        const int total = WebhookEventSink.QueueCapacity + excess;
        for (int i = 1; i < total; i++)
            sink.Emit(Row($"row-{i:D5}"));

        release.SetResult();
        await sink.DisposeAsync();

        HashSet<string> seenIds = [.. handler.Requests.Select(r => r.DeliveryId)];

        // The oldest of the FLOODED rows (comfortably away from row 0, which was already in flight
        // when the queue started filling) were displaced and never attempted.
        Assert.DoesNotContain("row-00001", seenIds);
        Assert.DoesNotContain("row-00005", seenIds);

        // The newest row still gets through — the entire reason DropOldest was chosen over
        // DropWrite: with any newest-loses policy the queue is full exactly when the terminal row
        // arrives, so the one row a CI wrapper exists to receive would be guaranteed lost.
        Assert.Contains($"row-{total - 1:D5}", seenIds);
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task EveryDroppedRowIsCounted()
    {
        var notices = new List<string>();
        var release = new TaskCompletionSource();
        var blockHeadParked = new TaskCompletionSource();
        var handler = new RecordingHandler
        {
            OnRequest = async (req, _, ct) =>
            {
                string id = DeliveryIdOf(req);
                if (id == "block-head")
                {
                    blockHeadParked.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                // Every flood row is a hard, non-retryable failure — one attempt each, so five of
                // them opens the circuit quickly once the pump is released.
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        // Source 1: full-queue displacement. Block the pump on the head row, then flood the queue —
        // the eviction count here is exact and independent of pump timing: `excess` rows enqueued
        // beyond capacity, so exactly the oldest `excess` of the flood are displaced.
        sink.Emit(Row("block-head"));

        // BARRIER, and the arithmetic below is wrong without it. Everything after this point assumes the
        // pump is PARKED in the handler holding block-head, so that the flood meets a queue nothing is
        // draining and exactly the oldest `excess` rows are displaced. Emitting block-head does not
        // establish that — it only makes it likely. Starve the pump (a concurrent whole-solution run is
        // enough, #566) and block-head is evicted by the flood before it is ever read; the handler's
        // blocking branch then never fires, the pump drains CONCURRENTLY with the writer, early flood
        // rows survive that should have been displaced, and flood-00000 gets the one POST this test
        // asserts it never gets. Measured exactly that way on ubuntu in the v1.17.0 release run.
        await blockHeadParked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        const int excess = 50;
        const int floodCount = WebhookEventSink.QueueCapacity + excess;
        for (int i = 0; i < floodCount; i++)
            sink.Emit(Row($"flood-{i:D5}"));

        release.SetResult();

        // Wait for the circuit to open: the surviving flood rows (the newest QueueCapacity of them) all
        // fail, so after 5 of them are attempted the circuit must be open. Wait on the LATCH rather than
        // on the fifth survivor's request arriving — the request is recorded before the row has even
        // been settled, let alone counted toward the threshold.
        await WaitFor(() => sink.CircuitIsOpen, "the circuit to open after five consecutive flood failures");

        // Source 2: arrival-drops. The circuit is open now, so these must never reach the handler.
        sink.Emit(Row("late-1"));
        sink.Emit(Row("late-2"));
        // TERMINAL-PHASE SENTINEL (design 3.3 step 3): DisposeAsync ALWAYS spends one attempt on the
        // LAST-ENQUEUED row, ignoring the circuit - that is precisely the guarantee
        // TerminalRowIsAttemptedWithTheCircuitOpen pins. Without a trailing row here, the row asserted
        // to get ZERO attempts would itself BE the terminal row and would be attempted exactly once,
        // so the assertion below would contradict the spec rather than test it.
        sink.Emit(Row("terminal-sentinel-b"));

        await sink.DisposeAsync();

        // Source 1 evidence.
        Assert.Equal(0, handler.CountFor("flood-00000"));
        Assert.Equal(0, handler.CountFor($"flood-{excess - 1:D5}"));

        // Source 2 evidence.
        Assert.Equal(0, handler.CountFor("late-1"));
        Assert.Equal(0, handler.CountFor("late-2"));

        // The reported total reconciles both sources.
        int droppedCount = ParseDroppedCount(notices);
        Assert.True(droppedCount >= excess + 2, $"expected at least {excess + 2} dropped, summary reported {droppedCount}");

        // Third case (§3.3 closing paragraph): Emit after DisposeAsync has returned writes to a
        // completed channel. TryWrite returns false there — a silent no-op, never a throw. Its count
        // cannot be reported (the summary has already printed); not throwing is the whole requirement.
        Exception? postDisposeThrow = Record.Exception(() => sink.Emit(Row("after-dispose")));
        Assert.Null(postDisposeThrow);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Teardown must never fail the run (§3.3 step 3, blocker B1)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task DisposeAsyncNeverThrowsWhenTheNoticeSinkThrows()
    {
        // `await using` puts the dispose in a compiler-emitted `finally` that spans past
        // `return exitCode;` in RunCommand.RunAsync, so an exception thrown in teardown replaces the
        // in-flight return and turns a wholly green run into an unhandled exception — and on the
        // fault path it replaces the original exception and destroys the diagnosis. A delivery
        // mechanism may never affect the run.
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(
            DefaultUrl, null, "guardrails/test",
            onNotice: _ => throw new InvalidOperationException("notice sink exploded"),
            handler, 0.05, cts.Token);

        sink.Emit(Row("row-1"));

        Exception? thrown = await Record.ExceptionAsync(async () => await sink.DisposeAsync());
        Assert.Null(thrown);
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task DisposeAsyncNeverThrowsWhenTheTransportThrows()
    {
        // Same rationale as DisposeAsyncNeverThrowsWhenTheNoticeSinkThrows: §3.3 step 3 wraps the
        // whole teardown body in catch (Exception) precisely so a throwing transport — on send, or on
        // its own Dispose — can never propagate past `await using` and destroy an in-flight return or
        // an in-flight fault's diagnosis.
        var notices = new List<string>();
        var handler = new ThrowingDisposeHandler();

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        for (int i = 0; i < 5; i++)
            sink.Emit(Row($"row-{i}"));

        Exception? thrown = await Record.ExceptionAsync(async () => await sink.DisposeAsync());
        Assert.Null(thrown);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The terminal row — the guarantee the whole feature exists for (§3.3 steps 2–3)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    public async Task EachRowIsPostedExactlyOnceOnAHealthyEndpoint()
    {
        // COMPLETENESS + NO-DUPLICATION, the two directions nothing else asserts. Every other test here
        // checks that a particular row WAS or WAS NOT attempted; none checks that the delivered set is
        // exactly the emitted set. An implementation that delivers half the rows, or that double-POSTs
        // the last one, passes the rest of this file.
        //
        // The last row is the one at risk: 3.3 step 3 spends a guaranteed terminal attempt on
        // _lastEnqueued, so if the "already settled" bookkeeping is wrong in the OTHER direction the
        // final row is POSTed twice on every green run - invisible to a receiver that dedupes on the
        // idempotency key, and a real defect for one that does not.
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        string[] ids = ["row-a", "row-b", "row-c", "row-final"];
        foreach (string id in ids)
        {
            sink.Emit(Row(id));
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (handler.CountFor(id) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await sink.DisposeAsync();

        foreach (string id in ids)
            Assert.Equal(1, handler.CountFor(id));

        Assert.Equal(ids.Length, handler.Requests.Count);
    }

    [Fact]
    public async Task TerminalRowIsAttemptedWithTheCircuitOpen()
    {
        // THE MOST IMPORTANT TEST IN THIS FILE — the blocker-3 regression test. `_lastEnqueued`
        // records the last row Emit SAW, whether or not the circuit's arrival-drop kept that row out
        // of the pump's path. If the circuit's arrival-drop also skipped the `_lastEnqueued` write,
        // this guarantee would evaporate in exactly the scenario it exists for.
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        // Open the circuit — the first four together, then the FIFTH ALONE, waiting for it to be
        // attempted before anything else is emitted.
        //
        // That last part is the whole point, and emitting all five back-to-back defeats it. A 400 is
        // non-retryable, so each row settles on its first attempt and calls MarkSettledIfLastEnqueued.
        // If the pump is BEHIND (all five emitted at once), _lastEnqueued is already "terminal-row" when
        // they settle, the delivery ids mismatch, and the settled flag never latches - so the guarantee
        // fires and this test passes without ever exercising the state it exists to protect. PRODUCTION
        // is the opposite: 8.1 calls this stream low-frequency, the pump keeps up, and a row settles
        // while it IS _lastEnqueued. This setup reproduces production.
        for (int i = 0; i < 4; i++)
            sink.Emit(Row($"open-{i}"));

        sink.Emit(Row("open-4"));

        // Wait on the LATCH. CircuitIsOpen is set inside OnPermanentFailure, AFTER that same method has
        // already called MarkSettledIfLastEnqueued — so observing the latch proves open-4 settled WHILE
        // it was _lastEnqueued, which is the exact state this test exists to protect. The previous form
        // waited for open-4's REQUEST to be recorded and then slept 50ms hoping settlement had followed;
        // that sleep is the race, and this is the state it was standing in for.
        await WaitFor(() => sink.CircuitIsOpen, "the circuit to open, which is also proof open-4 settled while it was _lastEnqueued");

        // Now let the transport succeed and emit one more, last-enqueued row.
        handler.OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        sink.Emit(Row("terminal-row"));

        await sink.DisposeAsync();

        // The circuit does not suppress the terminal delivery: §3.3 step 3 always spends exactly one
        // attempt on the last-enqueued row, whatever the circuit says.
        Assert.Equal(1, handler.CountFor("terminal-row"));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task TerminalRowIsAttemptedWithABacklogPending()
    {
        // The other half of blocker 3, and the likelier failure in the field: a slow-but-alive
        // endpoint backs the serial pump up near the end of a run without ever tripping the failure
        // threshold, so the terminal row sits behind a backlog. Teardown abandons the retry budget
        // entirely (one attempt per row through the backlog) — retrying during teardown is precisely
        // what starves the terminal row.
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        const double scale = 0.05; // BacklogDrainBudget 10s -> 500ms
        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, scale, cts.Token);

        for (int i = 0; i < 20; i++)
            sink.Emit(Row($"backlog-{i:D3}"));
        sink.Emit(Row("terminal-row"));

        await sink.DisposeAsync();

        Assert.Equal(1, handler.CountFor("terminal-row"));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task TeardownAbandonsTheRetryBudgetOneAttemptPerRow()
    {
        // §3.3 step 1 sets `_draining` and step 2 says what it buys: from there the pump makes "one
        // attempt per row — it abandons the retry budget entirely", because "retrying during teardown
        // is what starves the terminal row". SSOT §8.3 promises the same thing to receivers: "At
        // teardown the harness stops retrying altogether — one attempt per row." The flag did not
        // exist, so a retryable failure during the drain still consumed the full four-attempt schedule
        // and the backlog budget behind it.
        var notices = new List<string>();
        var release = new TaskCompletionSource();
        var handler = new RecordingHandler
        {
            OnRequest = async (req, _, ct) =>
            {
                if (DeliveryIdOf(req) == "head")
                {
                    await release.Task.WaitAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                // 503 is RETRYABLE (§5.1) — outside teardown this row would be POSTed four times.
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        };

        // scale 0.2: the backlog drain budget is 10s * 0.2 = 2s, comfortably longer than the retry
        // schedule this test asserts does NOT happen (first retry would land 100-300ms in), so a
        // truncated schedule cannot be mistaken for an exhausted budget.
        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.2, cts.Token);

        // Park the pump inside the head row's handler so the two rows behind it are still QUEUED when
        // teardown begins — that is the state §3.3 step 2 is about.
        sink.Emit(Row("head"));
        await WaitFor(() => handler.CountFor("head") >= 1, "the pump to be parked inside the head row");

        sink.Emit(Row("drain-me"));
        sink.Emit(Row("terminal-sentinel"));

        // DisposeAsync runs step 1 SYNCHRONOUSLY — the writer completion and the `_draining` set both
        // precede its first await — so by the time this call has returned its ValueTask the sink is
        // draining, and releasing the pump now is deterministic rather than a race.
        ValueTask disposal = sink.DisposeAsync();
        release.SetResult();
        await disposal;

        // EXACTLY ONE. Not "at most four": one attempt per row is the promise, and the retryable status
        // is what makes the number discriminating.
        Assert.Equal(1, handler.CountFor("drain-me"));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task TeardownNeverReportsAFailureForARowItNeverSent()
    {
        // A HEALTHY receiver — 200 to everything it is actually given — must never be reported as
        // having failed. After step 4 cancels the pump's token the pump keeps draining the channel, and
        // every row still in it gets an already-cancelled per-row token, breaks out of the attempt loop
        // having sent NOTHING, and used to fall straight through to the permanent-failure path. That
        // buffered a "delivery failed" notice and incremented the consecutive-failure counter for a row
        // that was never POSTed — measured, with 40 rows behind one slow row, as 6 "delivery failed"
        // notices plus "gave up after 5 consecutive delivery failures" against an endpoint that had
        // answered 200 every single time.
        //
        // Nothing was learned about the endpoint, because nothing was sent to it: a never-attempted row
        // is a plain counted drop, and the counts line plus §8.3's reconciliation path (diff the
        // received (bracket, seq) set against events.jsonl) is the whole of what it is owed.
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        const double scale = 0.05; // BacklogDrainBudget 10s -> 500ms, so most of the backlog is left over
        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, scale, cts.Token);

        for (int i = 0; i < 40; i++)
            sink.Emit(Row($"backlog-{i:D3}"));
        sink.Emit(Row("terminal-row"));

        await sink.DisposeAsync();

        // Positive control FIRST: rows really were left undelivered, so the assertions below are not
        // satisfied by a run in which the drain happened to finish.
        int dropped = ParseDroppedCount(notices);
        Assert.True(dropped > 0, $"this test needs an undrained backlog to mean anything; the summary reported {dropped} dropped");

        // THE HEADLINE: no aggregate verdict against the endpoint. The circuit must stay closed and
        // nothing may claim the harness "gave up after 5 consecutive delivery failures", because the
        // endpoint produced no failures at all.
        Assert.False(sink.CircuitIsOpen, "a receiver that answered 200 to every request it was given must never open the circuit");
        Assert.DoesNotContain(notices, n => n.Contains("gave up", StringComparison.Ordinal));
        Assert.DoesNotContain(notices, n => n.Contains("further delivery failure", StringComparison.Ordinal));

        // AT MOST ONE per-row failure notice, and its cause is the harness's own cancellation — not the
        // 39 queued rows. Exactly one row can be IN FLIGHT when step 4 cancels the pump's token (the
        // pump is serial), and that row genuinely was sent and genuinely was not delivered, so a notice
        // naming TaskCanceledException is honest about it. Every row BEHIND it was never sent at all,
        // and it is the count that separates the two: before the fix each of those produced its own
        // notice and its own _consecutiveFailures increment, which is what manufactured the "gave up"
        // line above against an endpoint that had answered 200 every time.
        List<string> failureNotices = [.. notices.Where(n => n.Contains("delivery failed", StringComparison.Ordinal))];
        Assert.True(
            failureNotices.Count <= 1,
            $"only the single in-flight row may be reported as failed; got {failureNotices.Count}:{Environment.NewLine}{string.Join(Environment.NewLine, failureNotices)}");
        foreach (string notice in failureNotices)
            Assert.Contains(nameof(TaskCanceledException), notice, StringComparison.Ordinal);

        // And the row the whole feature exists for still got its guaranteed attempt.
        Assert.Equal(1, handler.CountFor("terminal-row"));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task CancelledPathUsesTheShortBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), WebhookEventSink.BacklogDrainBudget);
        Assert.Equal(TimeSpan.Zero, WebhookEventSink.BacklogDrainBudgetCancelled);
        Assert.Equal(TimeSpan.FromSeconds(10), WebhookEventSink.TerminalDeliveryTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), WebhookEventSink.TerminalDeliveryTimeoutCancelled);

        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // the run's token is already cancelled before the sink is even asked to tear down

        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        for (int i = 0; i < 10; i++)
            sink.Emit(Row($"backlog-{i:D2}"));
        sink.Emit(Row("terminal-row"));

        await sink.DisposeAsync();

        // The budget is selected by the token; the drain itself never observes it. The backlog rows
        // are not attempted AT ALL when the run was already cancelled.
        for (int i = 0; i < 10; i++)
            Assert.Equal(0, handler.CountFor($"backlog-{i:D2}"));

        // Exactly one attempt is still spent on the last-enqueued row — always, circuit or no
        // circuit, backlog or no backlog.
        Assert.Equal(1, handler.CountFor("terminal-row"));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task CancelledTeardownFitsInsideTheProcessBudget()
    {
        // Every teardown budget has a cancelled variant — the pump shutdown grace did not, so a Ctrl-C
        // teardown spent the full 2s grace on top of the 500ms terminal attempt. MEASURED: DisposeAsync
        // took 2510 ms on a cancelled run, against the 2 s the whole process was then given after SIGINT
        // (System.CommandLine's default ProcessTerminationTimeout) — and before logServer.DisposeAsync()
        // and its own 5 s drain had even started. #603 has since set a deliberate 15 s ceiling, derived
        // partly FROM these budgets, so keeping them frugal is what keeps that ceiling honest.
        //
        // The production trigger needs no hostile fake: .NET's DNS resolution is not reliably
        // cancellable, so `--on-event https://does-not-resolve/` plus Ctrl-C parks the pump inside
        // SendAsync for the whole grace. The handler below reproduces exactly that — it ignores its own
        // cancellation token, which is the only property of the real case that matters.
        Assert.Equal(TimeSpan.FromSeconds(2), WebhookEventSink.PumpShutdownGrace);
        Assert.Equal(TimeSpan.FromMilliseconds(250), WebhookEventSink.PumpShutdownGraceCancelled);

        // The contract as pure arithmetic, independent of any machine's timing: the three cancelled
        // budgets must SUM to less than the process budget, since they are spent in series.
        TimeSpan cancelledTeardownBudget =
            WebhookEventSink.BacklogDrainBudgetCancelled
            + WebhookEventSink.TerminalDeliveryTimeoutCancelled
            + WebhookEventSink.PumpShutdownGraceCancelled;
        Assert.True(
            cancelledTeardownBudget < TimeSpan.FromSeconds(2),
            $"the cancelled teardown budget is {cancelledTeardownBudget}, which does not fit inside the ~2s the process gets after SIGINT (#603)");

        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = async (_, _, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10)); // ignores its token, exactly like DNS resolution
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        // timeScale 1.0, deliberately: this test is ABOUT the real wall-clock cost of a Ctrl-C teardown,
        // so scaling the budgets down would scale away the thing being measured.
        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, timeScale: 1.0, cts.Token);

        // The pump must be PARKED INSIDE SendAsync before the token is cancelled: once the run's token
        // is cancelled the pump drops rows on arrival without attempting them (§3.3), so a sink that was
        // already cancelled at construction has a pump that returns instantly and the grace is never
        // spent. Ctrl-C during an in-flight POST is the real shape.
        sink.Emit(Row("in-flight"));
        await WaitFor(() => handler.CountFor("in-flight") >= 1, "the pump to be parked inside an in-flight POST");

        sink.Emit(Row("terminal-row"));
        cts.Cancel();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await sink.DisposeAsync();
        stopwatch.Stop();

        // WHICH BUDGET was selected, not how long the machine took to run it. The wall-clock form of this
        // assertion (elapsed < 2s) FAILED on a contended windows CI runner at 2.374s while passing locally
        // at 977ms - for the same code, whose cancelled budgets sum to 750ms. Elapsed time cannot separate
        // "our budget is too big" from "this runner is busy", so it cannot be the assertion; the arithmetic
        // check above owns the contract, and this owns the decision.
        Assert.Equal(WebhookEventSink.PumpShutdownGraceCancelled, sink.LastPumpGraceUsed);

        // A deliberately LOOSE wall-clock sanity bound. It is here to catch a catastrophic regression (the
        // unscaled 2s grace plus the 500ms terminal attempt was measured at 2510ms of BUDGET alone), not to
        // police the budget - that is the arithmetic assertion's job. It must stay far enough above the
        // budget sum to survive a busy runner.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(8),
            $"a cancelled teardown took {stopwatch.Elapsed}, which is far beyond any plausible scheduling overhead on the 750ms of cancelled budget (#603)");

        // Positive control: the terminal attempt was actually SPENT, not skipped. Without this the
        // assertion above could be satisfied by a teardown that gave up on the guarantee the whole
        // feature exists for.
        Assert.True(
            stopwatch.Elapsed >= WebhookEventSink.TerminalDeliveryTimeoutCancelled,
            $"the terminal attempt was never spent: teardown took only {stopwatch.Elapsed}");

        // Second positive control: the pump really was still stuck, so the grace was the bound that
        // ended step 4 rather than a pump that had already returned.
        Assert.Contains(notices, n => n.Contains("stopped early", StringComparison.Ordinal));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task AFaultedPumpIsReportedNotSummarizedAsZero()
    {
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            // Ignores its own cancellation token entirely — Task.WhenAny(pump, delay) does not throw
            // on a faulted/hung pump, so a summary reading "0 dropped" while rows sit in a dead
            // channel would be the silent disappearance §2.2 mocks the shell shim for. A finite delay
            // comfortably longer than the scaled teardown budget — never Timeout.Infinite, which would
            // leave a task hanging for the life of the test host.
            OnRequest = async (_, _, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        const double scale = 0.05;
        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, scale, cts.Token);

        for (int i = 0; i < 5; i++)
            sink.Emit(Row($"row-{i}"));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await sink.DisposeAsync();
        stopwatch.Stop();

        // DisposeAsync still completes within its bounds, even with the pump stuck inside SendAsync
        // well past teardown's budget.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"DisposeAsync did not honor its bounds: {stopwatch.Elapsed}");

        // Positive control: notices were produced at all.
        Assert.NotEmpty(notices);

        string faultNotice = Assert.Single(
            notices, n => n.Contains("stopped early", StringComparison.Ordinal));
        Assert.Contains("never attempted", faultNotice, StringComparison.Ordinal);

        // The fault notice must NAME a non-zero number of unreached rows — that count is the whole
        // reason it is not a silent disappearance, and "0 row(s) never attempted" beside a stalled pump
        // would be the same defect wearing a different sentence.
        Match neverAttempted = Regex.Match(faultNotice, @"(\d+) row\(s\) never attempted");
        Assert.True(neverAttempted.Success, $"the fault notice does not name a count: {faultNotice}");
        Assert.True(
            int.Parse(neverAttempted.Groups[1].Value) > 0,
            $"the fault notice claims no rows were left unreached, with the pump stuck inside SendAsync: {faultNotice}");

        // UPDATED EXPECTATION, and the previous one was the right instinct expressed as the wrong
        // mechanism. This test used to assert `DoesNotContain("0 dropped", faultNotice)`, which held
        // because the counts line was printed only in the `else` of this branch — i.e. it was WITHHELD
        // on exactly the path where an operator most needs the numbers. §5.4 requires the counts line on
        // EVERY run that used --on-event: "a line that always prints is proof the mechanism ran at all",
        // and it is listed there alongside the stopped-early form, not as an alternative to it. So the
        // counts line prints here too, and the stopped-early notice prints IN ADDITION.
        //
        // What makes a "0 dropped" reading honest on this path is the line beside it naming the rows
        // that were never reached — the pair, not the suppression of half of it. That pairing is what
        // this test now pins, and it is strictly more than it asserted before: the old assertion was
        // satisfiable by printing nothing at all.
        string summary = Assert.Single(
            notices, n => n.Contains("delivered", StringComparison.Ordinal) && n.Contains("dropped", StringComparison.Ordinal));
        Assert.Contains("->", summary, StringComparison.Ordinal);
        Assert.True(
            notices.IndexOf(summary) < notices.IndexOf(faultNotice),
            "the counts line comes first and the stopped-early notice qualifies it; reversing them reads as a summary that supersedes the warning");
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task PerRowFailureNoticesAreCappedAndCollapsed()
    {
        Assert.Equal(2, WebhookEventSink.DeliveryFailureNoticeCap);

        // §5.3's circuit is the only thing bounding the per-row "delivery failed" notice, and it bounds
        // it ONLY while failures are CONSECUTIVE. A flapping receiver resets the counter on every
        // success, so the circuit never opens and the buffer grows one line per failed row. MEASURED:
        // 400 rows against a receiver alternating 200/400 produced 201 console lines, burying the §5.4
        // summary and the green-but-undelivered warning that prints beside it.
        const int rows = 400;
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            // Even rows succeed, odd rows are a hard non-retryable 400: one attempt each, and never two
            // failures in a row, so _consecutiveFailures never reaches 5.
            OnRequest = (req, _, _) =>
            {
                bool ok = DeliveryIdOf(req).StartsWith("ok-", StringComparison.Ordinal);
                return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest));
            }
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        for (int i = 0; i < rows; i++)
            sink.Emit(Row(i % 2 == 0 ? $"ok-{i:D3}" : $"bad-{i:D3}"));

        // Drain the whole burst BEFORE tearing down: teardown abandons the retry budget and drops what
        // is left, so a dispose that lands mid-burst would cut the failure count this test is counting.
        await WaitFor(() => handler.Requests.Count >= rows, $"all {rows} rows to be attempted", timeoutSeconds: 30);

        await sink.DisposeAsync();

        // Positive control: the receiver really did flap, and the circuit really did stay closed — which
        // is what makes the cap the only bound in play.
        Assert.False(sink.CircuitIsOpen, "alternating success/failure must never open the circuit; that is why the cap is needed at all");

        const int failures = rows / 2;

        // Exactly two individual failure notices, and one collapse line for the rest.
        Assert.Equal(
            WebhookEventSink.DeliveryFailureNoticeCap,
            notices.Count(n => n.Contains("delivery failed (", StringComparison.Ordinal)));

        string collapsed = Assert.Single(notices, n => n.Contains("further delivery failure", StringComparison.Ordinal));
        Assert.Contains($"{failures - WebhookEventSink.DeliveryFailureNoticeCap} further delivery failures", collapsed, StringComparison.Ordinal);

        // The collapse carries the LAST failure's description, so the suppressed lines cost no
        // information the operator did not already have twice over — and it stays inside §5.4's closing
        // rule (a status code or an exception TYPE NAME, never ex.Message, never the URL).
        Assert.Contains("400", collapsed, StringComparison.Ordinal);
        Assert.DoesNotContain(DefaultUrl.AbsolutePath, collapsed, StringComparison.Ordinal);

        // The summary is no longer buried: 201 lines becomes a handful, and the counts are exact.
        Assert.True(notices.Count <= 5, $"expected a handful of notices, got {notices.Count}:{Environment.NewLine}{string.Join(Environment.NewLine, notices)}");

        string summary = Assert.Single(
            notices, n => n.Contains("delivered", StringComparison.Ordinal) && n.Contains("dropped", StringComparison.Ordinal));
        Assert.Contains($"{failures} delivered", summary, StringComparison.Ordinal);
        Assert.Contains($"{failures} dropped", summary, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // No notice ever prints a credential (§5.4's closing rule, and §6.6)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task NoNoticeTextEverContainsTheAuthValue()
    {
        const string authValue = "Bearer sup3r-s3cret-t0k3n";
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (req, _, _) =>
            {
                return DeliveryIdOf(req) switch
                {
                    // A retryable status that exhausts every attempt.
                    "exhausts-retries" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                    // A hard 4xx.
                    "hard-failure" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
                    // A transport exception whose own Message carries the whole request URI AND the
                    // auth value — the case that actually matters, since HttpRequestException's
                    // message routinely carries the whole URI.
                    "transport-throws" => Task.FromException<HttpResponseMessage>(
                        new HttpRequestException($"Failed connecting to {req.RequestUri} with header {authValue}")),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
                };
            }
        };

        using var cts = new CancellationTokenSource();
        // scale 0.2, NOT 0.05. At 0.05 the per-attempt timeout is 10s * 0.05 = 500ms, and under load the
        // handler's 401 loses that race - the row is recorded as a TaskCanceledException instead, the
        // positive control below finds no "401"/"Unauthorized" among the notices, and the test fails
        // having proven nothing about secret redaction. Widening the budget keeps every assertion intact;
        // this test measures notice CONTENT, never elapsed time.
        var sink = new WebhookEventSink(DefaultUrl, authValue, "guardrails/test", notices.Add, handler, 0.2, cts.Token);

        sink.Emit(Row("exhausts-retries"));
        sink.Emit(Row("hard-failure"));
        sink.Emit(Row("transport-throws"));

        await sink.DisposeAsync();

        // Positive control: notices were produced, and they name the exception TYPE and the status —
        // "contains no secret" must not be satisfied by having produced nothing.
        Assert.NotEmpty(notices);
        Assert.Contains(notices, n => n.Contains("HttpRequestException", StringComparison.Ordinal));
        Assert.Contains(notices, n => n.Contains("401", StringComparison.Ordinal) || n.Contains("Unauthorized", StringComparison.Ordinal));

        foreach (string notice in notices)
        {
            Assert.DoesNotContain(authValue, notice, StringComparison.Ordinal);
            Assert.DoesNotContain("sup3r-s3cret-t0k3n", notice, StringComparison.Ordinal);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task NoNoticeTextEverContainsTheUrlPath()
    {
        // For Slack and webhook.site the URL PATH IS THE CREDENTIAL — this test (and
        // NoNoticeTextEverContainsTheAuthValue) is the reason RedactUrl exists.
        var url = new Uri("https://hooks.example.com/services/T00/B11/XyZ?token=abc");
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(url, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        // Zero drops: the summary must still print, because silence on success is the exact defect
        // this whole issue is about, and covering it here proves the mechanism ran at all.
        sink.Emit(Row("row-1"));

        await sink.DisposeAsync();

        Assert.NotEmpty(notices); // positive control

        string summary = Assert.Single(
            notices, n => n.Contains("delivered", StringComparison.Ordinal) && n.Contains("dropped", StringComparison.Ordinal));

        // Positive control: the host DOES appear, so this cannot pass on an empty/blank notice.
        Assert.Contains("hooks.example.com", summary, StringComparison.Ordinal);

        foreach (string notice in notices)
        {
            Assert.DoesNotContain("services", notice, StringComparison.Ordinal);
            Assert.DoesNotContain("T00", notice, StringComparison.Ordinal);
            Assert.DoesNotContain("B11", notice, StringComparison.Ordinal);
            Assert.DoesNotContain("XyZ", notice, StringComparison.Ordinal);
            Assert.DoesNotContain("token=abc", notice, StringComparison.Ordinal);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The production construction path — three values only TryStart decides (§6.5, §5.2)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task TryStartBuildsANonRedirectingClientAtRealTimeScale()
    {
        // §6.5: leaving AllowAutoRedirect at its default `true` would let a redirect move the POST —
        // with its Authorization header and its payload — to a host the operator never named, and it
        // would also make IsRetryable's 3xx row silently dead code (a following client never surfaces
        // a 3xx to classify). A stray debugging timeScale left in TryStart would ship a 10ms
        // per-attempt timeout and instant retries in production, and nothing red anywhere would say
        // so. This is the only test in this file that calls the REAL TryStart.
        var notices = new List<string>();

        // A loopback URL on a port nothing is listening on. TryStart builds a client; it does not
        // connect. No HttpListener, no bound port — with nothing enqueued, DisposeAsync's terminal
        // phase has no _lastEnqueued to attempt, so not one byte goes near the network.
        var url = new Uri("http://127.0.0.1:1/on-event"); // port 1 is reserved, never listened on

        using var cts = new CancellationTokenSource();
        WebhookEventSink? sink = WebhookEventSink.TryStart(url, null, "guardrails/test", notices.Add, cts.Token);

        Assert.NotNull(sink);
        Assert.Equal(false, sink!.HandlerAllowsAutoRedirect);
        Assert.Equal(1.0, sink.TimeScale);

        await sink.DisposeAsync();

        // The control that makes the above mean something: through the internal test constructor,
        // with a fake handler and a scale of our own choosing, both readbacks flip. A task-07
        // implementation that hard-codes `=> false` or `=> 1.0` would pass the TryStart side above and
        // fail this side.
        var fakeHandler = new RecordingHandler();
        var testSink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, fakeHandler, 0.01, cts.Token);

        Assert.Null(testSink.HandlerAllowsAutoRedirect);
        Assert.Equal(0.01, testSink.TimeScale);

        await testSink.DisposeAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The response-body cap (§5.2)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task ResponseBodyIsCappedAtEightKilobytes()
    {
        Assert.Equal(8 * 1024, WebhookEventSink.ResponseBodyReadCapBytes);

        byte[] body = new byte[64 * 1024]; // far larger than the cap
        Array.Fill(body, (byte)'x');
        var countingStream = new CountingStream(new MemoryStream(body));

        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(countingStream)
            })
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        sink.Emit(Row("row-1"));
        await sink.DisposeAsync();

        // The cap is enforced, not merely declared: HttpClient.MaxResponseContentBufferSize would
        // instead THROW when the body exceeds the limit, turning a delivered row into a failure.
        Assert.True(
            countingStream.BytesRead <= WebhookEventSink.ResponseBodyReadCapBytes,
            $"read {countingStream.BytesRead} bytes, expected <= {WebhookEventSink.ResponseBodyReadCapBytes}");

        // Positive control: the body is read (and discarded) at all — reading it is what releases the
        // connection. A response never read at all would satisfy "<= 8192" while proving nothing, and
        // HttpClient's default HttpCompletionOption.ResponseContentRead would instead buffer the WHOLE
        // 64 KB body before this code could cap anything.
        Assert.True(countingStream.BytesRead > 0);

        // Capping the read must not turn a delivered (any 2xx) row into a failure.
        string summary = Assert.Single(notices, n => n.Contains("delivered", StringComparison.Ordinal));
        Assert.Contains("1 delivered", summary, StringComparison.Ordinal);
        Assert.Contains("0 dropped", summary, StringComparison.Ordinal);
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task A2xxWhoseResponseBodyFaultsIsStillExactlyOneDelivery()
    {
        // §4.4 and SSOT §8.3 state the receiver contract verbatim: "Any 2xx is success. The response
        // body is ignored entirely." The drain exists ONLY to release the connection, so a fault while
        // reading it means the connection is already gone — there is nothing left to release and
        // nothing to report, and above all nothing about the STATUS LINE has changed.
        //
        // Deciding `delivered` after the drain instead of before it made a body-read fault reclassify
        // an accepted delivery as a retryable failure: measured as FOUR POSTs of a row the receiver had
        // already accepted at 200, then reported to the operator as a drop. The triggers are ordinary —
        // a receiver that answers 200 and closes without its declared Content-Length body, an
        // ngrok/cloudflared tunnel resetting, a proxy truncating — so a real receiver could be POSTed
        // the same task-settled row four times and still be told the run had dropped it.
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultingStream())
            })
        };

        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, 0.05, cts.Token);

        sink.Emit(Row("accepted-at-200"));
        await WaitFor(() => handler.CountFor("accepted-at-200") >= 1, "the row to be POSTed at least once");

        await sink.DisposeAsync();

        // ONE POST, not four: the 2xx settled the row on its first attempt.
        Assert.Equal(1, handler.CountFor("accepted-at-200"));

        // …and it is reported as delivered, not dropped. Both halves matter: an implementation that
        // stopped retrying but still counted the row a drop would satisfy the assertion above while
        // still telling the operator a receiver that accepted the row had rejected it.
        string summary = Assert.Single(
            notices, n => n.Contains("delivered", StringComparison.Ordinal) && n.Contains("dropped", StringComparison.Ordinal));
        Assert.Contains("1 delivered", summary, StringComparison.Ordinal);
        Assert.Contains("0 dropped", summary, StringComparison.Ordinal);

        // No failure was reported at all — a 2xx is not a failure however its body behaved.
        Assert.DoesNotContain(notices, n => n.Contains("delivery failed", StringComparison.Ordinal));
    }
}
