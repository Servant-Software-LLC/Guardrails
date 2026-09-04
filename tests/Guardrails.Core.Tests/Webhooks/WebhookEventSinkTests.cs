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
    /// Asserts <paramref name="actual"/> falls inside <paramref name="scaledStep"/>'s jittered band
    /// (§5.2: <c>[0.5, 1.5)</c>), plus generous slack for timer resolution and scheduler noise —
    /// Windows' own timer granularity alone is ~15 ms.
    /// </summary>
    private static void AssertWithinJitteredBand(TimeSpan actual, TimeSpan scaledStep, TimeSpan slack)
    {
        TimeSpan lower = scaledStep * WebhookEventSink.JitterLowerBound - slack;
        if (lower < TimeSpan.Zero)
            lower = TimeSpan.Zero;
        TimeSpan upper = scaledStep * WebhookEventSink.JitterUpperBound + slack;
        Assert.InRange(actual, lower, upper);
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
        var notices = new List<string>();
        var handler = new RecordingHandler
        {
            OnRequest = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };

        const double scale = 0.2; // 1/2/4s steps -> 200/400/800ms; keeps the test fast but measurable
        using var cts = new CancellationTokenSource();
        var sink = new WebhookEventSink(DefaultUrl, null, "guardrails/test", notices.Add, handler, scale, cts.Token);

        sink.Emit(Row("row-1"));

        // WAIT FOR THE SCHEDULE, do not race the teardown. Disposing immediately makes this test a coin
        // flip on jitter even on an idle machine: the jittered backoff can reach (1+2+4) * 1.5 * 0.2 =
        // 2.1s, while DisposeAsync's backlog budget is 10s * 0.2 = 2.0s - and the backlog phase abandons
        // retries (one attempt per row), so the fourth request is simply never made. Observed as
        // "Expected: 4 / Actual: 3". Poll for the schedule to complete, then tear down.
        DateTime attemptsDeadline = DateTime.UtcNow.AddSeconds(30);
        while (handler.Requests.Count < 4 && DateTime.UtcNow < attemptsDeadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await sink.DisposeAsync();

        IReadOnlyList<(string DeliveryId, DateTimeOffset At)> requests = handler.Requests;
        Assert.Equal(4, requests.Count);

        TimeSpan gap1 = requests[1].At - requests[0].At;
        TimeSpan gap2 = requests[2].At - requests[1].At;
        TimeSpan gap3 = requests[3].At - requests[2].At;

        TimeSpan slack = TimeSpan.FromMilliseconds(150);
        AssertWithinJitteredBand(gap1, WebhookEventSink.BackoffSteps[0] * scale, slack);
        AssertWithinJitteredBand(gap2, WebhookEventSink.BackoffSteps[1] * scale, slack);
        AssertWithinJitteredBand(gap3, WebhookEventSink.BackoffSteps[2] * scale, slack);

        // The one gap comparison that is exact regardless of jitter, since 2.0x always exceeds 1.5x:
        // jitter exists so a parallel burst of failures does not resynchronize, not to blur the shape.
        Assert.True(gap3 > gap1);
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
        DateTime openDeadline = DateTime.UtcNow.AddSeconds(10);
        while (handlerA.CountFor("fail-5") == 0 && DateTime.UtcNow < openDeadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

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
        DateTime circuitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.CountFor("open-5") == 0 && DateTime.UtcNow < circuitDeadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(handler.CountFor("open-5") >= 1, "the fifth failing row must be attempted before the transport is flipped — otherwise the circuit never opens and this test proves nothing");

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
        var handler = new RecordingHandler
        {
            OnRequest = async (req, _, ct) =>
            {
                string id = DeliveryIdOf(req);
                if (id == "block-head")
                {
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
        const int excess = 50;
        const int floodCount = WebhookEventSink.QueueCapacity + excess;
        for (int i = 0; i < floodCount; i++)
            sink.Emit(Row($"flood-{i:D5}"));

        release.SetResult();

        // Wait for the circuit to open: the surviving flood rows (the newest QueueCapacity of them)
        // all fail, so after 5 of them are attempted the circuit must be open.
        string fifthSurvivor = $"flood-{excess + 4:D5}";
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.CountFor(fifthSurvivor) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

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

        // Open the circuit.
        for (int i = 0; i < 5; i++)
            sink.Emit(Row($"open-{i}"));

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
        Assert.DoesNotContain("0 dropped", faultNotice, StringComparison.Ordinal);
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
}
