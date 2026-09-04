using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #517 — the stall bound counted machine SLEEP as silence, so a laptop asleep longer than the
/// bound woke to a watchdog that killed the session instantly and reported <c>stalled</c>. The process
/// was SUSPENDED, not wedged: the diagnosis was wrong in the direction that looks authoritative, and
/// #504's whole argument was that a bound must measure the property it claims to measure.
///
/// <para><b>Every clause asserts a DECISION, never elapsed time.</b> A suspend is a clock JUMP, so a test
/// that produced one by actually sleeping would be measuring the machine — and could not tell "the rule is
/// wrong" from "this runner is busy". <see cref="StallWatch"/> takes an injected clock and records
/// <see cref="StallWatch.LastVerdict"/> / <see cref="StallWatch.SuspendsObserved"/> for exactly this
/// (the <c>WebhookEventSink.LastPumpGraceUsed</c> precedent).</para>
///
/// <para><see cref="TransientClassificationTests"/> covers the pure rule
/// (<see cref="StallWatch.Classify"/>); this file covers the STATE it moves — which is where the defect
/// actually lived, since the shipped rule was never wrong about anything it was asked.</para>
/// </summary>
public sealed class StallWatchTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(20);   // WaveBreakdownInvoker's

    /// <summary>
    /// A clock the test moves by hand. Ticks, not <c>DateTime</c>, because that is what the watch reads —
    /// and moving it is the only honest way to simulate a machine that was not running.
    /// </summary>
    private sealed class FakeClock
    {
        private long _ticks = DateTime.UtcNow.Ticks;

        internal long Now() => _ticks;

        internal void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static (StallWatch Watch, FakeClock Clock) NewWatch()
    {
        var clock = new FakeClock();
        return (new StallWatch(Bound, clock.Now), clock);
    }

    [Fact]
    public void AMachineThatSleptIsNotKilledAsStalled()
    {
        // The reported case: a laptop asleep for two hours. The wall clock shows six times the bound of
        // "silence" on the first poll after resume — but the session had no opportunity to emit, and
        // killing it here names a bound it never violated.
        (StallWatch watch, FakeClock clock) = NewWatch();

        clock.Advance(TimeSpan.FromHours(2));
        StallVerdict verdict = watch.Observe();

        Assert.Equal(StallVerdict.Suspended, verdict);
        Assert.False(watch.Stalled);
    }

    [Fact]
    public void TheSilenceWindowIsRESET_NotMerelySpared()
    {
        // The half a "did it kill anything?" assertion cannot see. Sparing the session on the resume poll
        // but leaving the window at two hours would kill it on the VERY NEXT poll, one interval later —
        // indistinguishable from a correct fix for exactly one poll. The session must get a fresh FULL
        // window, so the poll after the resume is an ordinary quiet poll.
        (StallWatch watch, FakeClock clock) = NewWatch();

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(StallVerdict.Suspended, watch.Observe());

        clock.Advance(watch.PollInterval);
        Assert.Equal(StallVerdict.KeepWaiting, watch.Observe());
        Assert.False(watch.Stalled);
        Assert.Equal(1, watch.SuspendsObserved);
    }

    [Fact]
    public void AGenuineStallIsStillKilled()
    {
        // The other half, and the one a suspend-tolerant watchdog is at risk of losing: polls arriving ON
        // SCHEDULE with nothing on the stream for longer than the bound. That is what the bound exists for.
        (StallWatch watch, FakeClock clock) = NewWatch();

        StallVerdict verdict = StallVerdict.KeepWaiting;
        for (int poll = 0; poll < 25; poll++)
        {
            clock.Advance(watch.PollInterval);
            verdict = watch.Observe();
            if (verdict == StallVerdict.Stalled)
            {
                break;
            }
        }

        Assert.Equal(StallVerdict.Stalled, verdict);
        Assert.True(watch.Stalled);
        Assert.Equal(0, watch.SuspendsObserved);
    }

    [Fact]
    public void AStallThatFollowsASuspendIsStillKilled()
    {
        // Tolerating suspends must not make the bound unenforceable: after the resume the session is given
        // a fresh window, and if it stays silent through THAT window it is killed, exactly as if the
        // suspend had never happened. A watchdog that could be permanently disarmed by one sleep would
        // trade #517 for a worse bug.
        (StallWatch watch, FakeClock clock) = NewWatch();

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(StallVerdict.Suspended, watch.Observe());

        StallVerdict verdict = StallVerdict.KeepWaiting;
        for (int poll = 0; poll < 25 && verdict != StallVerdict.Stalled; poll++)
        {
            clock.Advance(watch.PollInterval);
            verdict = watch.Observe();
        }

        Assert.Equal(StallVerdict.Stalled, verdict);
    }

    [Fact]
    public void AStreamingSessionIsNeverKilled_HoweverLongItTakes()
    {
        // The bound is on SILENCE, not duration (#504). Four hours of a session that keeps emitting must
        // pass untouched — the property the retired 30-minute wall clock destroyed.
        (StallWatch watch, FakeClock clock) = NewWatch();

        for (int poll = 0; poll < 240; poll++)
        {
            clock.Advance(watch.PollInterval);
            watch.Beat();                       // the stream produced something in this interval
            Assert.Equal(StallVerdict.KeepWaiting, watch.Observe());
        }

        Assert.False(watch.Stalled);
    }

    [Fact]
    public void OrdinarySchedulingJitterStillKillsAGenuineStall()
    {
        // The suspend test is a gap of ORDERS OF MAGNITUDE, not a margin. A poll arriving 3x late is a
        // loaded machine, not a suspended one, and a genuine stall underneath it must still be caught —
        // otherwise the fix would quietly convert every busy CI runner into an unbounded session.
        (StallWatch watch, FakeClock clock) = NewWatch();

        StallVerdict verdict = StallVerdict.KeepWaiting;
        for (int poll = 0; poll < 25 && verdict != StallVerdict.Stalled; poll++)
        {
            clock.Advance(watch.PollInterval * 3);
            verdict = watch.Observe();
        }

        Assert.Equal(StallVerdict.Stalled, verdict);
        Assert.Equal(0, watch.SuspendsObserved);
    }

    [Fact]
    public void ThePollCadenceIsWellUnderTheBound()
    {
        // The discrimination only works because the loop polls far more often than the bound: a two-hour
        // gap is unambiguous in a one-minute loop and meaningless in a twenty-minute one. Both runners
        // computed this independently before; it is stated once now, and pinned here.
        (StallWatch watch, _) = NewWatch();

        Assert.Equal(TimeSpan.FromMinutes(1), watch.PollInterval);
        Assert.Equal(Bound, watch.Bound);
    }

    [Fact]
    public void ATinyBoundNeverSpinsThePool()
    {
        // A bound/20 cadence under a second would burn a pool thread on a test-sized bound. The floor is
        // one second, and it does not change the VERDICT — a stall at a 5-second bound is still caught.
        var clock = new FakeClock();
        var watch = new StallWatch(TimeSpan.FromSeconds(5), clock.Now);

        Assert.Equal(TimeSpan.FromSeconds(1), watch.PollInterval);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(StallVerdict.KeepWaiting, watch.Observe());

        for (int poll = 0; poll < 4; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            watch.Observe();
        }

        Assert.True(watch.Stalled);
    }
}
