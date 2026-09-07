namespace Guardrails.Core.Execution;

/// <summary>Which schedule produced the most recent pause — the DECISION, exposed so a test can assert it.</summary>
public enum TransientHorizon
{
    /// <summary>Bounded exponential (2s, 4s, 8s … capped at 60s): a blip — an overload, a brief 503.</summary>
    Exponential,

    /// <summary>Probe polling toward a named or assumed reset: a quota/session limit measured in hours.</summary>
    Poll
}

/// <summary>
/// The backoff policy for a transient, retryable infrastructure pause (SSOT §9, issues #115 and #511).
/// A transient signal (HTTP 429/503/529, "overloaded", a usage/session/rate limit) must NOT consume the
/// retry budget: the harness backs off and re-runs the SAME unit of work.
///
/// <para><b>Two horizons, one mechanism (#511).</b> A provider failure is not one condition, it is two
/// with wildly different time constants, and a single schedule serves one of them badly:</para>
///
/// <list type="table">
///   <item>
///     <term><see cref="TransientHorizon.Exponential"/></term>
///     <description>An overload that clears in seconds. 2s, 4s, 8s … capped at 60s. Polling this every
///     half hour would turn a five-second hiccup into a lost half hour.</description>
///   </item>
///   <item>
///     <term><see cref="TransientHorizon.Poll"/></term>
///     <description>A session/quota cap that clears at a named hour. <c>nextProbe = min(resetInstant,
///     now + probeInterval)</c>, retrying the real unit of work until it succeeds. Hammering this every
///     60 seconds for four hours is a few hundred pointless requests.</description>
///   </item>
/// </list>
///
/// <para><b>The discriminator is the reset hint</b>, per the maintainer ruling on #511: a limit that tells
/// you when it clears is by construction the long-horizon kind. A door with no cheap short retry — the wave
/// barrier, whose alternative to waiting is ENDING THE RUN and re-paying a whole breakdown — passes
/// <c>pollOnly</c> and skips the exponential phase entirely.</para>
///
/// <para><b>Why poll rather than sleep to the stated reset.</b> The stated time is an upper bound, not a
/// schedule; providers move these limits and frequently reset EARLY. A probe that is wrong costs one second
/// and $0.00 — measured on the run that reported #511 — and a probe that succeeds simply IS the run
/// continuing, so there is nothing cheaper to ping and no reason to invent a synthetic one.</para>
///
/// <para>The actual waiting is delegated to an injected delay function so tests gate it deterministically
/// (no real sleeps); production passes <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. The horizon
/// each pause chose is exposed on <see cref="LastHorizon"/> so tests assert the DECISION rather than timing
/// the wait — a duration cannot tell a wrong policy from a loaded machine (#518).</para>
/// </summary>
public sealed class TransientBackoff
{
    /// <summary>Base of the exponential schedule: 2s, 4s, 8s, … (issue #115).</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>Per-pause ceiling on the exponential horizon, so a blip never blocks the run unreasonably long.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>Probe cadence on the poll horizon when nothing sooner is known (<c>providerProbeIntervalMinutes</c>).</summary>
    public static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromMinutes(30);

    /// <summary>Floor on the probe cadence, so a misconfigured interval can never busy-loop the provider.</summary>
    public static readonly TimeSpan MinProbeInterval = TimeSpan.FromMinutes(1);

    /// <summary>Total bound on a poll-horizon wait (<c>maxProviderWaitHours</c>) — long enough to cover a night.</summary>
    public static readonly TimeSpan DefaultProviderWaitBound = TimeSpan.FromHours(12);

    private readonly TimeSpan _budget;
    private readonly TimeSpan _probeInterval;
    private readonly TimeSpan _providerWaitBound;
    private readonly bool _pollOnly;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;

    private TimeSpan _elapsed = TimeSpan.Zero;
    private int _pauseCount;
    private bool _pollUsed;

    /// <param name="budget">
    /// The cumulative wall-clock pause budget on the EXPONENTIAL horizon (<c>transientPauseBudgetSeconds</c>).
    /// When non-positive, <see cref="IsEnabled"/> is false and a transient signal is treated as a normal
    /// failure (pausing disabled entirely, both horizons).
    /// </param>
    /// <param name="delay">Injected wait; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="probeInterval">Poll-horizon cadence; defaults to <see cref="DefaultProbeInterval"/>.</param>
    /// <param name="providerWaitBound">
    /// Total bound on a poll-horizon wait; defaults to <see cref="DefaultProviderWaitBound"/>. Set it to
    /// <see cref="TimeSpan.Zero"/> to disable the poll horizon outright — the policy then reduces exactly to
    /// the pre-#511 bounded exponential, which is the off switch for an interactive session that would rather
    /// fail fast than wait out a quota.
    /// </param>
    /// <param name="pollOnly">
    /// True for a door where the exponential horizon is pointless because there is no cheap retry to make —
    /// the wave barrier, whose alternative to waiting is ending the run and re-paying a whole breakdown.
    /// </param>
    /// <param name="now">Injected clock, for resolving a reset hint deterministically in tests.</param>
    public TransientBackoff(
        TimeSpan budget,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? probeInterval = null,
        TimeSpan? providerWaitBound = null,
        bool pollOnly = false,
        Func<DateTimeOffset>? now = null)
    {
        _budget = budget;
        _delay = delay ?? Task.Delay;
        // Floored, not trusted: a zero or negative cadence would make the poll horizon a tight loop
        // hammering the provider that is already refusing — the one behaviour this whole policy exists to
        // avoid. The off switch is providerWaitBound = 0, which disables the horizon outright; it is not a
        // zero interval, and a config that says otherwise is honoured as one probe per minute.
        TimeSpan cadence = probeInterval ?? DefaultProbeInterval;
        _probeInterval = cadence < MinProbeInterval ? MinProbeInterval : cadence;
        _providerWaitBound = providerWaitBound ?? DefaultProviderWaitBound;
        _pollOnly = pollOnly;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>False when the pause budget is non-positive — pausing is disabled entirely.</summary>
    public bool IsEnabled => _budget > TimeSpan.Zero;

    /// <summary>How many pauses have been taken so far for this unit of work (for the observer/log).</summary>
    public int PauseCount => _pauseCount;

    /// <summary>Cumulative time spent paused for this unit of work so far.</summary>
    public TimeSpan Elapsed => _elapsed;

    /// <summary>
    /// The horizon the most recent <see cref="NextDelay"/> selected — the decision this class makes, exposed
    /// so a test asserts WHICH schedule ran rather than how long the wait happened to take (#518).
    /// </summary>
    public TransientHorizon LastHorizon { get; private set; } = TransientHorizon.Exponential;

    /// <summary>The reset instant the most recent <see cref="NextDelay"/> resolved, or null when it had no usable hint.</summary>
    public DateTimeOffset? LastResolvedReset { get; private set; }

    /// <summary>Whether the poll horizon is available at all (it is switched off by a zero wait bound).</summary>
    public bool PollHorizonEnabled => _providerWaitBound > TimeSpan.Zero;

    /// <summary>
    /// The next wait, and the horizon that produced it. On the poll horizon this is
    /// <c>min(resetInstant, now + probeInterval)</c> — never longer than the probe interval, which is what
    /// makes an imperfectly-resolved hint safe (see <see cref="ProviderResetHint"/>). Clamped so it never
    /// overshoots the remaining bound for its horizon. Records the decision; does not spend the budget.
    /// </summary>
    public TimeSpan NextDelay(string? resetHint = null)
    {
        DateTimeOffset now = _now();
        DateTimeOffset? reset = ProviderResetHint.Resolve(resetHint, now);
        LastResolvedReset = reset;

        bool poll = PollHorizonEnabled && (_pollOnly || reset is not null);
        LastHorizon = poll ? TransientHorizon.Poll : TransientHorizon.Exponential;
        _pollUsed |= poll;

        TimeSpan delay;
        if (poll)
        {
            delay = _probeInterval;
            if (reset is { } instant)
            {
                TimeSpan untilReset = instant - now;
                if (untilReset < delay)
                {
                    // A reset sooner than the cadence — the short rate-limit case. Never negative: Resolve
                    // only ever returns an instant strictly ahead of `now`.
                    delay = untilReset;
                }
            }
        }
        else
        {
            // 2 * 2^pauseCount, guarded against overflow at large counts (caps at MaxDelay long before).
            double seconds = BaseDelay.TotalSeconds * Math.Pow(2, Math.Min(_pauseCount, 16));
            delay = TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
        }

        TimeSpan remaining = ActiveBound - _elapsed;
        return remaining < delay ? remaining : delay;
    }

    /// <summary>
    /// True when another pause still fits within the bound for the CURRENT horizon. False once the
    /// cumulative pause has reached it — the caller then settles the unit of work with a rate-limit reason
    /// rather than waiting forever on a permanently revoked key.
    /// </summary>
    public bool CanPauseAgain() => IsEnabled && _elapsed < ActiveBound;

    /// <summary>
    /// Wait the <see cref="NextDelay"/> and record the pause against the budget. Caller must have checked
    /// <see cref="CanPauseAgain"/> first. Returns the delay actually waited.
    /// </summary>
    public async Task<TimeSpan> PauseAsync(CancellationToken cancellationToken, string? resetHint = null)
    {
        TimeSpan delay = NextDelay(resetHint);
        await _delay(delay, cancellationToken).ConfigureAwait(false);
        _elapsed += delay;
        _pauseCount++;
        return delay;
    }

    /// <summary>
    /// The bound the cumulative pause is judged against. Exponential waits are bounded by
    /// <c>transientPauseBudgetSeconds</c>; the moment a POLL wait is taken, this unit of work is in the
    /// long-horizon world and <c>maxProviderWaitHours</c> governs from then on — including any later
    /// exponential pause.
    ///
    /// <para><b>Latched on <c>_pollUsed</c>, deliberately, rather than read off the CURRENT horizon.</b> The
    /// obvious version — bound = the bound belonging to whichever horizon this pause is on — has a silent
    /// failure that a real test caught: one 30-minute poll wait spends 30 minutes of the meter, and the next
    /// pause, being un-hinted, is judged as EXPONENTIAL against a budget the poll has already consumed. With
    /// a 30-minute exponential budget that pause is clamped to <b>zero seconds</b>: the harness reports a
    /// pause, waits not at all, and hammers the provider it had just decided to wait for. Measured as
    /// "expected 00:00:06, actual 00:30:00" — the second wait silently gone.</para>
    ///
    /// <para>Latching also reads correctly: a task that has met a session limit is in the long-wait world,
    /// and a 503 arriving during that window should not be judged against the budget for blips.</para>
    /// </summary>
    private TimeSpan ActiveBound =>
        _pollUsed && PollHorizonEnabled ? _providerWaitBound : _budget;
}
