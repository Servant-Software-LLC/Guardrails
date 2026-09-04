namespace Guardrails.Core.Prompts;

/// <summary>What a single stall-watchdog poll concluded (issues #504 / #517).</summary>
public enum StallVerdict
{
    /// <summary>Silence is within the bound; keep polling.</summary>
    KeepWaiting,

    /// <summary>The MACHINE was not running between polls (sleep / hibernate / a hard freeze).</summary>
    Suspended,

    /// <summary>The session has genuinely produced nothing for longer than the bound.</summary>
    Stalled
}

/// <summary>
/// The stall watchdog's state and its ONE decision rule, shared by every runner that bounds SILENCE
/// (<see cref="PromptInvocation.StallBound"/>): the last-activity clock the stream reader beats, the
/// poll cadence, and <see cref="Observe"/> — the per-poll verdict, plus the window reset a suspend
/// demands.
///
/// <para><b>Why it is one object and not a rule each runner spells for itself.</b> #517 shipped the
/// suspend discrimination into <see cref="ClaudePromptRunner"/> only.
/// <see cref="OpenAiCompatPromptRunner"/> — the LOCAL-INFERENCE runner, i.e. the one an unattended
/// overnight run is most likely to be sitting on — kept the naked
/// <c>if (SilentFor() &lt; bound) continue;</c> and would still kill a healthy session on wake. Two
/// copies of a bound is how one of them stays wrong; there is now one, and both runners call it.</para>
///
/// <para><b>The clock is <see cref="DateTime.UtcNow"/> ON PURPOSE, and that is the fix rather than the
/// bug.</b> The obvious repair for "the wall clock counts suspend" is a monotonic clock — and on Windows
/// there is no such clock available here. Measured on the operator's Windows 11 machine, 4.8 days after
/// boot, at one instant:</para>
/// <code>
/// QueryUnbiasedInterruptTime  359,578 s   (excludes sleep BY DEFINITION)
/// Environment.TickCount64     413,689 s
/// Stopwatch / QPC             413,723 s
/// wall clock since boot       413,698 s
/// </code>
/// <para>Both candidate "monotonic" clocks track the wall clock and are ~15 hours AHEAD of unbiased
/// time — i.e. both count suspend on Windows. (They do exclude it on Linux and macOS, where they map to
/// <c>CLOCK_MONOTONIC</c> / <c>mach_absolute_time</c>; the divergence is Windows-specific, and Windows is
/// the platform this defect was reported from.) Swapping the clock would have looked like a fix and
/// changed nothing. So the discrimination is made a different way: a clock that DOES advance across
/// suspend is what makes the gap between two consecutive polls visible at all, and that gap is the
/// evidence. The wall clock is a required input to the mechanism.</para>
///
/// <para>Only <c>QueryUnbiasedInterruptTime</c> genuinely excludes suspend on Windows, and reaching it
/// means a per-OS P/Invoke in a cross-platform core for a signal the poll gap already carries — the
/// trade the poll-gap detector exists to avoid.</para>
/// </summary>
public sealed class StallWatch
{
    /// <summary>
    /// How much later than its own interval a poll must arrive before the machine is judged to have been
    /// SUSPENDED rather than merely loaded.
    ///
    /// <para><b>This is not a heuristic about load.</b> The watchdog polls at <c>bound / 20</c> — about
    /// 60 seconds at the shipped 20-minute bound — so the two cases are separated by orders of magnitude,
    /// not by a margin: a two-hour gap in a one-minute loop is unambiguous. The factor only has to be
    /// larger than the worst scheduling delay a RUNNING machine can impose.</para>
    ///
    /// <para><b>And the failure direction is the safe one.</b> Misreading a genuine stall as a suspend
    /// costs one more bound-length window before the kill; misreading a suspend as a stall KILLS HEALTHY
    /// WORK, which is the whole defect #504 set out to remove. When the two are hard to tell apart,
    /// wait.</para>
    /// </summary>
    internal const int SuspendFactor = 4;

    private readonly Func<long> _nowTicks;

    /// <summary>Written by the stream-reader thread, read by the watchdog — via <see cref="Volatile"/>.</summary>
    private long _lastActivityTicks;

    /// <summary>Touched only by the single watchdog thread inside <see cref="Observe"/>.</summary>
    private long _previousPollTicks;

    private int _stalled;
    private int _suspends;
    private int _lastVerdict;

    /// <summary>A watch on the real wall clock — the production constructor.</summary>
    /// <param name="bound">How long a session may be SILENT before it is killed.</param>
    public StallWatch(TimeSpan bound)
        : this(bound, static () => DateTime.UtcNow.Ticks)
    {
    }

    /// <summary>
    /// A watch over an injected clock. Internal because the injection exists for ONE reason: a suspend is
    /// a clock jump, and a test that produced one by actually suspending the machine (or by sleeping) would
    /// be measuring the machine rather than the decision.
    /// </summary>
    internal StallWatch(TimeSpan bound, Func<long> nowTicks)
    {
        ArgumentNullException.ThrowIfNull(nowTicks);

        _nowTicks = nowTicks;
        Bound = bound;

        // The cadence both runners computed independently, now stated once: a twentieth of the bound, and
        // never tighter than a second (a tiny bound in a test must not spin the pool).
        PollInterval = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, bound.Ticks / 20));

        long now = nowTicks();
        _lastActivityTicks = now;
        _previousPollTicks = now;
    }

    /// <summary>How long a session may be SILENT before <see cref="Observe"/> returns <see cref="StallVerdict.Stalled"/>.</summary>
    public TimeSpan Bound { get; }

    /// <summary>How long a watchdog should wait between <see cref="Observe"/> calls.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>
    /// True once <see cref="Observe"/> has returned <see cref="StallVerdict.Stalled"/>. Set by the
    /// watchdog thread and read after the run unwinds, so it goes through <see cref="Volatile"/> rather
    /// than a lock — the same spawn-guard shape as the #452 fail-fast flag beside it.
    /// </summary>
    public bool Stalled => Volatile.Read(ref _stalled) == 1;

    /// <summary>
    /// The verdict the most recent <see cref="Observe"/> reached, exposed so a test can assert WHICH
    /// decision the watchdog made rather than how long the machine took to reach it. Elapsed time cannot
    /// separate "the rule is wrong" from "this runner is busy"; the decision is the thing under test.
    /// </summary>
    internal StallVerdict LastVerdict => (StallVerdict)Volatile.Read(ref _lastVerdict);

    /// <summary>
    /// How many polls concluded <see cref="StallVerdict.Suspended"/> — the other half of the same
    /// observation, so a test can prove the window was RESET rather than merely that nothing was killed
    /// (a watchdog that never fired for the wrong reason looks identical from outside).
    /// </summary>
    internal int SuspendsObserved => Volatile.Read(ref _suspends);

    /// <summary>The session produced output — restart the silence window. Called on every teed line / streamed frame.</summary>
    public void Beat() => Volatile.Write(ref _lastActivityTicks, _nowTicks());

    /// <summary>
    /// How long the session has been silent, for the operator-facing summary. NOT the kill decision —
    /// that is <see cref="Observe"/>, which knows about suspends; this is a wall-clock reading and will
    /// include a suspend that happened inside the window.
    /// </summary>
    public TimeSpan SilentFor() => TimeSpan.FromTicks(_nowTicks() - Volatile.Read(ref _lastActivityTicks));

    /// <summary>
    /// One poll's decision, and the state it moves. <see cref="StallVerdict.Suspended"/> RESETS the
    /// silence window — the session had no opportunity to emit while the machine was not running, so
    /// counting that time as silence measures elapsed time again with a smaller number, which is exactly
    /// what #504 removed. <see cref="StallVerdict.Stalled"/> latches <see cref="Stalled"/>.
    ///
    /// <para>Called only from the watchdog loop, one thread.</para>
    /// </summary>
    public StallVerdict Observe()
    {
        long pollAt = _nowTicks();
        var sincePreviousPoll = TimeSpan.FromTicks(pollAt - _previousPollTicks);
        var silent = TimeSpan.FromTicks(pollAt - Volatile.Read(ref _lastActivityTicks));
        _previousPollTicks = pollAt;

        StallVerdict verdict = Classify(silent, sincePreviousPoll, PollInterval, Bound);
        Volatile.Write(ref _lastVerdict, (int)verdict);

        switch (verdict)
        {
            case StallVerdict.Suspended:
                // A fresh FULL window, not a partial credit: the session is being given the chance to
                // emit that the suspend took away.
                Volatile.Write(ref _lastActivityTicks, pollAt);
                Interlocked.Increment(ref _suspends);
                break;

            case StallVerdict.Stalled:
                Volatile.Write(ref _stalled, 1);
                break;
        }

        return verdict;
    }

    /// <summary>
    /// The watchdog LOOP: poll on <see cref="PollInterval"/>, and call <paramref name="onStalled"/> exactly
    /// once if a poll ever concludes <see cref="StallVerdict.Stalled"/>. Returns when it fires, or when
    /// <paramref name="cancellationToken"/> ends the run it was policing.
    ///
    /// <para><b><paramref name="delay"/> is injected for ONE reason.</b> A suspend is a clock jump, so a
    /// test that produced one by sleeping would be measuring the machine rather than the decision, and
    /// #517's own filing named this as why the defect was untestable: "the current code reads
    /// <c>DateTime.UtcNow</c> directly". Production passes <see cref="Task.Delay(TimeSpan,
    /// CancellationToken)"/>; a test passes a delay that advances its own clock and returns completed, so
    /// the whole loop runs deterministically with no sleeps at all.</para>
    ///
    /// <para>Both cancellation shapes end the loop QUIETLY. <see cref="OperationCanceledException"/> is the
    /// ordinary "the run finished" path, and <see cref="ObjectDisposedException"/> is the launch-failure
    /// one — a caller can return and dispose its token source out from under a watchdog still sitting in
    /// the delay, and an unobserved exception on a pool thread is a poor way to report that a process never
    /// started.</para>
    /// </summary>
    public async Task WatchAsync(
        Func<TimeSpan, CancellationToken, Task> delay,
        Action onStalled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(onStalled);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (Observe() is not StallVerdict.Stalled)
            {
                continue;
            }

            onStalled();
            return;
        }
    }

    /// <summary>
    /// The rule itself, pure and separately testable: a poll that took vastly longer than its own interval
    /// means the machine was SUSPENDED, not that the session went silent (issue #517). Otherwise the
    /// ordinary #504 comparison decides.
    /// </summary>
    internal static StallVerdict Classify(
        TimeSpan silent, TimeSpan sincePreviousPoll, TimeSpan poll, TimeSpan bound)
    {
        if (poll > TimeSpan.Zero && sincePreviousPoll > poll * SuspendFactor)
        {
            return StallVerdict.Suspended;
        }

        return silent >= bound ? StallVerdict.Stalled : StallVerdict.KeepWaiting;
    }
}
