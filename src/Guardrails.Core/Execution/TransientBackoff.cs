namespace Guardrails.Core.Execution;

/// <summary>
/// The bounded backoff policy for a transient, retryable infrastructure pause (SSOT §9, issue #115).
/// A transient signal (HTTP 429/503/529, "overloaded", a usage/session/rate limit) must NOT consume
/// the retry budget: the harness backs off and re-runs the SAME attempt. This policy answers two
/// questions for each pause: how long to wait (bounded exponential, capped), and whether the task's
/// cumulative pause has exhausted the wall-clock budget (the named bound on "a rate limit must never
/// mark needs-human" — only an exhausted budget halts).
///
/// <para>The actual waiting is delegated to an injected delay function so tests gate it
/// deterministically (no real sleeps); production passes <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</para>
/// </summary>
public sealed class TransientBackoff
{
    /// <summary>Base of the exponential schedule: 2s, 4s, 8s, … (issue #115).</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>Per-pause ceiling so a single backoff never blocks the run unreasonably long.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _budget;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private TimeSpan _elapsed = TimeSpan.Zero;
    private int _pauseCount;

    /// <param name="budget">
    /// The cumulative wall-clock pause budget for one task (<c>transientPauseBudgetSeconds</c>). When
    /// non-positive, <see cref="IsEnabled"/> is false and a transient signal is treated as a normal
    /// failure (pausing disabled).
    /// </param>
    /// <param name="delay">Injected wait; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public TransientBackoff(TimeSpan budget, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _budget = budget;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>False when the pause budget is non-positive — pausing is disabled entirely.</summary>
    public bool IsEnabled => _budget > TimeSpan.Zero;

    /// <summary>How many pauses have been taken so far for this task (for the observer/log).</summary>
    public int PauseCount => _pauseCount;

    /// <summary>Cumulative time spent paused for this task so far.</summary>
    public TimeSpan Elapsed => _elapsed;

    /// <summary>
    /// The next backoff delay (bounded exponential, capped at <see cref="MaxDelay"/>), CLAMPED so it
    /// never overshoots the remaining budget. Pure — does not mutate state.
    /// </summary>
    public TimeSpan NextDelay()
    {
        // 2 * 2^pauseCount, guarded against overflow at large counts (caps at MaxDelay long before).
        double seconds = BaseDelay.TotalSeconds * Math.Pow(2, Math.Min(_pauseCount, 16));
        var delay = TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));

        TimeSpan remaining = _budget - _elapsed;
        return remaining < delay ? remaining : delay;
    }

    /// <summary>
    /// True when another pause still fits within the budget. False once the cumulative pause has
    /// reached the budget — the caller then settles the task <c>needs-human</c> with a rate-limit reason.
    /// </summary>
    public bool CanPauseAgain() => IsEnabled && _elapsed < _budget;

    /// <summary>
    /// Wait the <see cref="NextDelay"/> and record the pause against the budget. Caller must have
    /// checked <see cref="CanPauseAgain"/> first. Returns the delay actually waited.
    /// </summary>
    public async Task<TimeSpan> PauseAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = NextDelay();
        await _delay(delay, cancellationToken).ConfigureAwait(false);
        _elapsed += delay;
        _pauseCount++;
        return delay;
    }
}
