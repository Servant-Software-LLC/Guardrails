using System.Net;

namespace Guardrails.Core.Execution;

/// <summary>
/// Delivers each `events.jsonl` row to an operator-supplied webhook endpoint (#585 layer 3 —
/// docs/plans/585-layer3-webhooks-contract.md). This file currently holds only the two PURE policy
/// functions (§5.1, §6.6) as throwing stubs and the §5.2 schedule as named constants — task 05
/// implements the two functions; task 06/07 add the channel, the pump, the <c>HttpClient</c> and
/// disposal, none of which belong here yet.
/// </summary>
public sealed class WebhookEventSink
{
    // ── §5.2 — the retry schedule, unasserted here; task 06's tests are what pin these numbers ──

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
