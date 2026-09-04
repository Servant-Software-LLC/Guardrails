using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Channels;

namespace Guardrails.Core.Execution;

/// <summary>
/// Delivers each `events.jsonl` row to an operator-supplied webhook endpoint (#585 layer 3 —
/// docs/plans/585-layer3-webhooks-contract.md). Task 05 implemented the two pure policy functions
/// (§5.1, §6.6). This file adds the queue, the pump, the circuit, the six-step teardown and the
/// production construction path (§3, §6.5).
/// </summary>
public sealed class WebhookEventSink : IAsyncDisposable
{
    private readonly Uri _url;
    private readonly string? _auth;
    private readonly string _userAgent;
    private readonly Action<string> _onNotice;
    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _client;
    private readonly double _timeScale;
    private readonly CancellationToken _runCancellationToken;
    private readonly Channel<EventDelivery> _channel;
    private readonly CancellationTokenSource _pumpCts;
    private readonly Task _pumpTask;

    private long _deliveredCount;
    private long _droppedCount;

    private readonly object _lastEnqueuedGate = new();
    private EventDelivery? _lastEnqueued;

    /// <summary>
    /// Whether the row currently held by <see cref="_lastEnqueued"/> has already reached a terminal
    /// outcome via NORMAL processing — delivered, or permanently failed after exhausting its own
    /// retry budget. The §3.3 step 3 guarantee exists to rescue a row that never got a fair shot at
    /// all (evicted from the queue, dropped on arrival by an open circuit, or still unprocessed when
    /// the backlog budget runs out) — not to pile an extra attempt onto a row that already had its
    /// full normal attempt budget and still failed.
    /// </summary>
    private bool _lastEnqueuedSettled;

    private volatile bool _circuitOpen;

    private readonly object _noticeGate = new();
    private int _consecutiveFailures;
    private string? _lastFailureDescription;
    private readonly List<string> _bufferedNotices = [];

    private static readonly TimeSpan PumpShutdownGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Production entry point. Returns null when there is no <c>--on-event</c> URL. Never throws: the
    /// CLI validates the URL EARLY, before any run state is touched (design §6.4, task 09).
    /// </summary>
    public static WebhookEventSink? TryStart(
        Uri? url, string? auth, string userAgent, Action<string> onNotice, CancellationToken cancellationToken)
    {
        if (url is null)
            return null;

        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        return new WebhookEventSink(url, auth, userAgent, onNotice, handler, timeScale: 1.0, cancellationToken);
    }

    /// <summary>
    /// TEST SEAM. Internal, and <c>Guardrails.Core.csproj</c> already carries
    /// <c>&lt;InternalsVisibleTo Include="Guardrails.Core.Tests" /&gt;</c> (measured: line 27).
    /// </summary>
    internal WebhookEventSink(
        Uri url, string? auth, string userAgent, Action<string> onNotice,
        HttpMessageHandler handler, double timeScale, CancellationToken cancellationToken)
    {
        _url = url;
        _auth = auth;
        _userAgent = userAgent;
        _onNotice = onNotice;
        _handler = handler;
        _timeScale = timeScale;
        _runCancellationToken = cancellationToken;

        _client = new HttpClient(handler, disposeHandler: true)
        {
            // §3.2: the per-attempt timeout is enforced by a CancellationTokenSource per request, not
            // by the client — this Timeout must never race that mechanism.
            Timeout = Timeout.InfiniteTimeSpan
        };

        _channel = Channel.CreateBounded<EventDelivery>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            },
            itemDropped: _ => Interlocked.Increment(ref _droppedCount));

        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <summary>
    /// PRODUCTION-ONLY READBACK, and it exists for exactly one test (behaviour 15 of
    /// <c>WebhookEventSinkTests</c>). <see cref="TryStart"/> is the ONLY path that decides this value,
    /// and every other test in that file substitutes the handler away through the internal
    /// constructor — so without this property there is no way, anywhere in this plan, to observe what
    /// <see cref="TryStart"/> actually built.
    ///
    /// <c>bool?</c> and not <c>bool</c>, deliberately: null when the sink's handler is NOT a
    /// <see cref="System.Net.Http.SocketsHttpHandler"/> (the injected-fake path), the flag's real
    /// value when it is. That nullability is what lets the test assert BOTH sides and so rule out a
    /// hard-coded <c>=&gt; false</c>.
    /// </summary>
    internal bool? HandlerAllowsAutoRedirect => (_handler as SocketsHttpHandler)?.AllowAutoRedirect;

    /// <summary>
    /// The scale the sink is ACTUALLY using: 1.0 on the <see cref="TryStart"/> path, whatever the
    /// internal constructor was handed on the test path.
    /// </summary>
    internal double TimeScale => _timeScale;

    /// <summary>The <c>Action&lt;EventDelivery&gt;</c> callback <c>RunEventStream</c> invokes inside its append lock.</summary>
    public void Emit(EventDelivery delivery)
    {
        try
        {
            // §3.2: record the last row Emit SAW, whether or not the circuit or a full queue kept it
            // out of the pump's path — this is what the terminal-phase guarantee (§3.3 step 3) reaches
            // for, and it is why the dispatcher needs no knowledge of the event vocabulary at all.
            lock (_lastEnqueuedGate)
            {
                _lastEnqueued = delivery;
            }

            if (_circuitOpen)
            {
                Interlocked.Increment(ref _droppedCount);
                return;
            }

            if (!_channel.Writer.TryWrite(delivery))
            {
                // The channel writer has already been completed (post-DisposeAsync emission, §3.3's
                // closing paragraph): a counted drop and a silent no-op, never a throw.
                Interlocked.Increment(ref _droppedCount);
            }
        }
        catch (Exception)
        {
            // Belt as well as the braces RunEventStream.AppendLine already puts around this callback:
            // Emit runs on a Scheduler worker thread inside RunEventStream's append lock, and a throw
            // here would propagate while holding that lock.
            Interlocked.Increment(ref _droppedCount);
        }
    }

    /// <summary>
    /// §3.3: DisposeAsync MUST NOT THROW. <c>await using</c> puts this dispose in a compiler-emitted
    /// <c>finally</c> spanning past <c>return exitCode;</c> in <c>RunCommand.RunAsync</c>, so an
    /// exception thrown here replaces the in-flight return (turning a wholly green run into an
    /// unhandled exception) and, on the fault path, destroys the diagnosis. A delivery failure can
    /// never change the run's exit code, verdict, journal, or timing beyond the bounded drain below.
    ///
    /// <para><b>Plan 35 §9.3, and the corrected rule this method exists to honor:</b> <c>LogServer</c>
    /// drained in-flight requests three lines too late, after the listener had already torn down the
    /// shared request queue, so its "best-effort" final delivery of <c>run-finished</c> failed every
    /// single time across ~10 measured variants — "a 'best-effort' mechanism that is 0% effective is
    /// not best-effort; it is dead code." <c>LogServer</c> always cancelled first; what moved was the
    /// drain, above the transport teardown. So the rule is not "cancel last":
    /// <b>signal wind-down first, drain second, tear the transport down last.</b> Layer 3's transport
    /// is the <see cref="HttpClient"/>, so that is what this method disposes last, after the pump has
    /// provably returned (or this method has given up waiting on it, bounded).</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            // Step 1: signal wind-down. Completing the writer is also what makes a post-dispose Emit's
            // TryWrite return false — a counted drop, never a throw.
            _channel.Writer.TryComplete();

            bool cancelled = _runCancellationToken.IsCancellationRequested;
            TimeSpan backlogBudget = Scaled(cancelled ? BacklogDrainBudgetCancelled : BacklogDrainBudget);
            TimeSpan terminalBudget = Scaled(cancelled ? TerminalDeliveryTimeoutCancelled : TerminalDeliveryTimeout);

            // Step 2: backlog phase. Bounded, and skipped entirely when the budget is zero (a cancelled
            // run) — the run's own token only ever selects THIS budget, it is never observed by the
            // drain itself, because an already-cancelled token would otherwise skip the drain outright.
            if (backlogBudget > TimeSpan.Zero)
            {
                await Task.WhenAny(_pumpTask, Task.Delay(backlogBudget)).ConfigureAwait(false);
            }

            // Step 3: terminal phase, which ALWAYS runs. Bounded via Task.WhenAny rather than trusting
            // the attempt's own cancellation to be honored — a misbehaving handler that ignores its
            // token must never be able to hold DisposeAsync hostage.
            EventDelivery? terminal;
            bool alreadySettled;
            lock (_lastEnqueuedGate)
            {
                terminal = _lastEnqueued;
                alreadySettled = _lastEnqueuedSettled;
            }

            if (terminal is { } terminalRow && !alreadySettled)
            {
                Task terminalAttempt = AttemptOnceAsync(terminalRow, CancellationToken.None, terminalBudget, trackCircuit: false);
                await Task.WhenAny(terminalAttempt, Task.Delay(terminalBudget)).ConfigureAwait(false);
            }

            // Step 4: cancel the pump's token, then await it, bounded. Disposing a CancellationTokenSource
            // while a wait is outstanding on it is undefined behaviour, so nothing may touch _pumpCts
            // until every dispatched request has returned — or, as a last resort against a transport that
            // never returns, until this bounded grace period gives up waiting for it.
            _pumpCts.Cancel();
            Task pumpWait = await Task.WhenAny(_pumpTask, Task.Delay(Scaled(PumpShutdownGrace))).ConfigureAwait(false);
            bool pumpStoppedCleanly = ReferenceEquals(pumpWait, _pumpTask);

            // Step 5: the transport goes LAST, after the pump has provably returned (or been given up on).
            try { _client.Dispose(); } catch (Exception) { /* never let transport teardown affect the run */ }
            try { _pumpCts.Dispose(); } catch (Exception) { /* ditto */ }

            // Step 6: emit the buffered notices + the summary, now that the counts are final and the
            // live table is long gone.
            lock (_noticeGate)
            {
                foreach (string line in _bufferedNotices)
                    SafeNotice(line);

                if (!pumpStoppedCleanly)
                {
                    long neverAttempted;
                    try { neverAttempted = _channel.Reader.Count; }
                    catch (NotSupportedException) { neverAttempted = 0; }

                    string desc = _lastFailureDescription ?? nameof(TaskCanceledException);
                    SafeNotice($"Webhook: delivery stopped early ({desc}); {neverAttempted} row(s) never attempted.");
                }
                else
                {
                    long delivered = Interlocked.Read(ref _deliveredCount);
                    long dropped = Interlocked.Read(ref _droppedCount);
                    SafeNotice($"Webhook: {delivered} delivered, {dropped} dropped -> {RedactUrl(_url)}");
                }
            }
        }
        catch (Exception)
        {
            // See the class doc above this method: DisposeAsync must never throw, on any path.
        }
    }

    private void SafeNotice(string line)
    {
        try { _onNotice(line); }
        catch (Exception) { /* a delivery mechanism may never affect the run — not even its own notice */ }
    }

    private TimeSpan Scaled(TimeSpan value) => value * _timeScale;

    private TimeSpan Jittered(TimeSpan step)
    {
        double factor = JitterLowerBound + ((JitterUpperBound - JitterLowerBound) * Random.Shared.NextDouble());
        return Scaled(step) * factor;
    }

    /// <summary>The pump: the ONE background task started in the constructor, reading serially so a retrying row delays later rows rather than being overtaken by them.</summary>
    private async Task PumpAsync()
    {
        try
        {
            await foreach (EventDelivery delivery in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    // The run's own cancellation token is never wired into an attempt's own SendAsync
                    // call (§3.3's rule), but it IS a valid reason for the pump to stop starting NEW
                    // work — that is what makes the cancelled-path backlog guarantee (zero attempts)
                    // reliable rather than a race against DisposeAsync's own bounded wait.
                    if (_runCancellationToken.IsCancellationRequested || _circuitOpen)
                    {
                        Interlocked.Increment(ref _droppedCount);
                        continue;
                    }

                    await AttemptWithRetriesAsync(delivery).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _droppedCount);
                }
            }
        }
        catch (Exception)
        {
            // Never let the pump task fault visibly — DisposeAsync observes its completion state via
            // Task.WhenAny, not by awaiting (and thus re-throwing) this task directly.
        }
    }

    private async Task AttemptWithRetriesAsync(EventDelivery delivery)
    {
        CancellationTokenSource rowCts;
        try
        {
            rowCts = CancellationTokenSource.CreateLinkedTokenSource(_pumpCts.Token);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        using (rowCts)
        {
            try { rowCts.CancelAfter(Scaled(PerRowCeiling)); } catch (ObjectDisposedException) { }

            AttemptOutcome last = default;

            for (int attempt = 1; attempt <= MaxAttemptsPerRow; attempt++)
            {
                if (rowCts.IsCancellationRequested)
                    break;

                using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(rowCts.Token);
                attemptCts.CancelAfter(Scaled(PerAttemptTimeout));

                last = await SendOnceAsync(delivery, attempt, attemptCts.Token).ConfigureAwait(false);

                if (last.Delivered)
                {
                    OnDelivered(delivery, trackCircuit: true);
                    return;
                }

                bool retryable = IsRetryable(last.Status, last.Error);
                if (!retryable || attempt == MaxAttemptsPerRow)
                    break;

                TimeSpan backoff = Jittered(BackoffSteps[attempt - 1]);
                try
                {
                    await Task.Delay(backoff, rowCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            OnPermanentFailure(delivery, last.Status, last.Error, trackCircuit: true);
        }
    }

    /// <summary>Used ONLY by the terminal phase (§3.3 step 3): exactly one attempt, ignoring the circuit and the backlog.</summary>
    private async Task AttemptOnceAsync(EventDelivery delivery, CancellationToken ct, TimeSpan timeout, bool trackCircuit)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try { cts.CancelAfter(timeout); } catch (ObjectDisposedException) { return; }

        AttemptOutcome outcome = await SendOnceAsync(delivery, attemptNumber: 1, cts.Token).ConfigureAwait(false);

        if (outcome.Delivered)
            OnDelivered(delivery, trackCircuit);
        else
            OnPermanentFailure(delivery, outcome.Status, outcome.Error, trackCircuit);
    }

    private void OnDelivered(EventDelivery delivery, bool trackCircuit)
    {
        Interlocked.Increment(ref _deliveredCount);

        if (trackCircuit)
        {
            lock (_noticeGate) { _consecutiveFailures = 0; }
        }

        MarkSettledIfLastEnqueued(delivery);
    }

    private void OnPermanentFailure(EventDelivery delivery, HttpStatusCode? status, Exception? error, bool trackCircuit)
    {
        Interlocked.Increment(ref _droppedCount);
        MarkSettledIfLastEnqueued(delivery);

        string description = DescribeFailure(status, error);

        lock (_noticeGate)
        {
            _lastFailureDescription = description;
            _bufferedNotices.Add($"Webhook: delivery failed ({description}).");

            // §5.3: after 5 CONSECUTIVE rows exhaust their attempts the circuit opens for the rest of
            // the run and never closes. Latched here, once — the notice is buffered rather than printed
            // live, because the pump is a background thread and RunCommand holds a Spectre Live region
            // open across the entire DAG (#145 Bug 1: any console write into an active Live region
            // corrupts the table).
            if (trackCircuit && !_circuitOpen)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= CircuitThreshold)
                {
                    _circuitOpen = true;
                    _bufferedNotices.Add(
                        $"Webhook: gave up at {DateTime.Now:HH:mm:ss} after {CircuitThreshold} consecutive delivery failures (last: {description}).");
                }
            }
        }
    }

    private void MarkSettledIfLastEnqueued(EventDelivery delivery)
    {
        lock (_lastEnqueuedGate)
        {
            if (_lastEnqueued is { } last && last.DeliveryId == delivery.DeliveryId)
                _lastEnqueuedSettled = true;
        }
    }

    private static string DescribeFailure(HttpStatusCode? status, Exception? error)
    {
        // Every notice prints the exception's TYPE NAME and the HTTP status code only — never
        // ex.Message (which routinely carries the whole request URI) and never the full URL.
        if (status is { } value)
            return $"{(int)value} {value}";

        return error?.GetType().Name ?? nameof(TaskCanceledException);
    }

    private async Task<AttemptOutcome> SendOnceAsync(EventDelivery delivery, int attemptNumber, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(delivery.JsonLine, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.TryAddWithoutValidation("X-Guardrails-Delivery-Id", delivery.DeliveryId);
            request.Headers.TryAddWithoutValidation("X-Guardrails-Event-Kind", delivery.Kind);
            request.Headers.TryAddWithoutValidation("X-Guardrails-Delivery-Attempt", attemptNumber.ToString());

            if (_auth is not null)
                request.Headers.TryAddWithoutValidation("Authorization", _auth);

            // ResponseHeadersRead, not the default ResponseContentRead: the default buffers the WHOLE
            // body before this code could cap anything, which is exactly what the 8 KB cap exists to
            // prevent (§5.2).
            using HttpResponseMessage response =
                await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            await DrainResponseBodyAsync(response, ct).ConfigureAwait(false);

            bool delivered = (int)response.StatusCode is >= 200 and < 300;
            return new AttemptOutcome(delivered, response.StatusCode, null);
        }
        catch (Exception ex)
        {
            return new AttemptOutcome(false, null, ex);
        }
    }

    /// <summary>
    /// Reads at most <see cref="ResponseBodyReadCapBytes"/> off the response stream, then discards it —
    /// releasing the connection without buffering a hostile response. <see cref="HttpClient.MaxResponseContentBufferSize"/>
    /// is not the answer: it THROWS past the limit, turning a delivered row into a failed one.
    /// </summary>
    private static async Task DrainResponseBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[4096];
        int totalRead = 0;

        while (totalRead < ResponseBodyReadCapBytes)
        {
            int toRead = Math.Min(buffer.Length, ResponseBodyReadCapBytes - totalRead);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read <= 0)
                break;

            totalRead += read;
        }
    }

    private readonly record struct AttemptOutcome(bool Delivered, HttpStatusCode? Status, Exception? Error);

    // ── §5.2 — the retry schedule ────────────────────────────────────────────────────────────────

    /// <summary>Initial attempt + retries allowed for one row before it is counted a drop (§5.2).</summary>
    internal const int MaxAttemptsPerRow = 4;

    /// <summary>Delay before each retry, before jitter is applied (§5.2, §5.3).</summary>
    internal static readonly TimeSpan[] BackoffSteps =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>Lower bound of the multiplicative jitter applied to each backoff step (§5.2).</summary>
    internal const double JitterLowerBound = 0.5;

    /// <summary>Upper bound (exclusive) of the multiplicative jitter applied to each backoff step (§5.2).</summary>
    internal const double JitterUpperBound = 1.5;

    /// <summary>Timeout for a single HTTP attempt (§5.2).</summary>
    internal static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Hard ceiling on the total time spent delivering one row, whatever the attempt timings do (§5.2).</summary>
    internal static readonly TimeSpan PerRowCeiling = TimeSpan.FromSeconds(45);

    /// <summary>Bounded channel capacity; it only fills when the pump is stalled (§3.2, §5.2).</summary>
    internal const int QueueCapacity = 1024;

    /// <summary>Consecutive terminally-failed rows before the circuit opens for the rest of the run (§5.3).</summary>
    internal const int CircuitThreshold = 5;

    /// <summary>Backlog drain budget on a normal teardown — one attempt per row, retries abandoned (§3.3 step 2, §5.2).</summary>
    internal static readonly TimeSpan BacklogDrainBudget = TimeSpan.FromSeconds(10);

    /// <summary>Backlog drain budget when the run was cancelled — the drain is skipped entirely (§3.3 step 2, §5.2).</summary>
    internal static readonly TimeSpan BacklogDrainBudgetCancelled = TimeSpan.Zero;

    /// <summary>Budget for the guaranteed single attempt at the last-enqueued row on a normal teardown (§3.3 step 3, §5.2).</summary>
    internal static readonly TimeSpan TerminalDeliveryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Terminal delivery budget when the run was cancelled — still always spent (§3.3 step 3, §5.2).</summary>
    internal static readonly TimeSpan TerminalDeliveryTimeoutCancelled = TimeSpan.FromMilliseconds(500);

    /// <summary>Cap on the response body read before it is discarded, so a hostile response cannot be buffered (§5.2, §6.5).</summary>
    internal const int ResponseBodyReadCapBytes = 8 * 1024;

    // ── §5.1 / §6.6 — the two pure functions ───────────────────────────────────────────────────

    /// <summary>Whether a failed delivery attempt should be retried (§5.1). Pure — no I/O, no clock.</summary>
    internal static bool IsRetryable(HttpStatusCode? status, Exception? error)
    {
        if (status is not HttpStatusCode value)
        {
            // No status was received at all — a transport-level exception (connection refused, DNS
            // failure, TLS handshake failure, socket error, per-attempt timeout, or anything else the
            // client threw). §5.1's last row is deliberately conservative: retry, whatever it was.
            return true;
        }

        int code = (int)value;

        return code switch
        {
            408 or 429 => true,
            >= 500 and <= 599 => true,
            _ => false,
        };
    }

    /// <summary>
    /// Renders <paramref name="url"/> as <c>&lt;scheme&gt;://&lt;host&gt;[:&lt;port&gt;]/…</c> — never
    /// the path, never the query, never the userinfo (§6.6). Public: both Core's runtime notices and
    /// the CLI's startup plain-<c>http</c> warning need it, and <c>Guardrails.Cli</c> is a separate
    /// assembly that <c>InternalsVisibleTo</c> does not cover.
    /// </summary>
    public static string RedactUrl(Uri url)
    {
        string port = url.IsDefaultPort ? string.Empty : $":{url.Port}";
        string elided = url.PathAndQuery == "/" ? string.Empty : "/…";
        return $"{url.Scheme}://{url.Host}{port}{elided}";
    }
}
