using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// The POLL horizon added by issue #511: a provider limit that names its own reset is a quota window
/// measured in hours, not a blip, and it polls toward the reset instead of hammering the exponential's 60s
/// ceiling for the whole budget.
///
/// <para><b>Every assertion here is on the DECISION, never on a duration.</b> <c>LastHorizon</c> says which
/// schedule ran and <c>LastResolvedReset</c> says what instant it worked from; both are exact. A test that
/// instead timed the wait could not tell a wrong policy from a loaded runner (#518), and the waits in
/// question are measured in half-hours, so it could not run at all.</para>
/// </summary>
public sealed class TransientBackoffPollHorizonTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 17, 51, 0, TimeSpan.FromHours(2)); // the instant on the reporting run

    private static (TransientBackoff backoff, List<TimeSpan> waited) Make(
        TimeSpan? probeInterval = null,
        TimeSpan? providerWaitBound = null,
        bool pollOnly = false,
        TimeSpan? budget = null,
        DateTimeOffset? now = null)
    {
        var waited = new List<TimeSpan>();
        var backoff = new TransientBackoff(
            budget ?? TimeSpan.FromHours(4),
            (d, _) => { waited.Add(d); return Task.CompletedTask; },
            probeInterval: probeInterval,
            providerWaitBound: providerWaitBound,
            pollOnly: pollOnly,
            now: () => now ?? Now);
        return (backoff, waited);
    }

    [Fact]
    public void NoHint_KeepsTheExponentialHorizon()
    {
        // The #115 behaviour is untouched. A brief 503 that says nothing about a reset is a blip, and
        // polling it every half hour would turn a five-second hiccup into a lost half hour.
        (TransientBackoff backoff, _) = Make();

        TimeSpan delay = backoff.NextDelay(resetHint: null);

        Assert.Equal(TransientHorizon.Exponential, backoff.LastHorizon);
        Assert.Null(backoff.LastResolvedReset);
        Assert.Equal(TransientBackoff.BaseDelay, delay);
    }

    [Fact]
    public void AResetHint_SwitchesToThePollHorizon()
    {
        // The discriminator, per the maintainer ruling: a limit that tells you when it clears is by
        // construction the long-horizon kind.
        (TransientBackoff backoff, _) = Make();

        backoff.NextDelay("8:30pm");

        Assert.Equal(TransientHorizon.Poll, backoff.LastHorizon);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 23, 20, 30, 0, TimeSpan.FromHours(2)),
            backoff.LastResolvedReset);
    }

    [Fact]
    public void PollOnly_PollsEvenWithNoHint()
    {
        // The barrier door. Its alternative to waiting is ending the run and re-paying a from-scratch
        // breakdown, so there is no cheap short retry for an exponential to be worth making — and the ruling
        // is explicit that the hint is an optimisation, never a dependency ("no hint → pure interval
        // polling").
        (TransientBackoff backoff, _) = Make(probeInterval: TimeSpan.FromMinutes(30), pollOnly: true);

        TimeSpan delay = backoff.NextDelay(resetHint: null);

        Assert.Equal(TransientHorizon.Poll, backoff.LastHorizon);
        Assert.Null(backoff.LastResolvedReset);
        Assert.Equal(TimeSpan.FromMinutes(30), delay);
    }

    [Fact]
    public void TheWaitIsMinOfResetAndProbeInterval_ResetSooner()
    {
        // The short rate-limit case, and what the `min` is FOR: a reset three minutes out must not be slept
        // through for a full probe interval.
        (TransientBackoff backoff, _) = Make(probeInterval: TimeSpan.FromMinutes(30));

        TimeSpan delay = backoff.NextDelay("5:54pm"); // 3 minutes after Now

        Assert.Equal(TransientHorizon.Poll, backoff.LastHorizon);
        Assert.Equal(TimeSpan.FromMinutes(3), delay);
    }

    [Fact]
    public void TheWaitIsMinOfResetAndProbeInterval_IntervalSooner()
    {
        // The session-cap case — and the property that makes imperfect hint parsing SAFE. A reset resolved
        // hours out (or resolved wrongly, in the wrong zone, on the wrong day) can never make the harness
        // wait longer than one probe interval, because the interval always wins the `min`.
        (TransientBackoff backoff, _) = Make(probeInterval: TimeSpan.FromMinutes(30));

        TimeSpan delay = backoff.NextDelay("8:30pm"); // 2h39m after Now

        Assert.Equal(TransientHorizon.Poll, backoff.LastHorizon);
        Assert.Equal(TimeSpan.FromMinutes(30), delay);
    }

    [Fact]
    public void AZeroWaitBound_DisablesThePollHorizonEntirely()
    {
        // The documented off switch for an interactive session that would rather fail fast than wait out a
        // quota: the policy reduces EXACTLY to the pre-#511 bounded exponential, hint or no hint.
        (TransientBackoff backoff, _) = Make(providerWaitBound: TimeSpan.Zero);

        TimeSpan delay = backoff.NextDelay("8:30pm");

        Assert.False(backoff.PollHorizonEnabled);
        Assert.Equal(TransientHorizon.Exponential, backoff.LastHorizon);
        Assert.Equal(TransientBackoff.BaseDelay, delay);
    }

    [Fact]
    public void AZeroProbeInterval_IsFlooredRatherThanBusyLooping()
    {
        // A misconfigured cadence must not become a tight loop hammering the provider that is already
        // refusing — the single behaviour this whole policy exists to avoid. Zero is a misconfiguration, not
        // the off switch; the off switch is a zero WAIT BOUND (asserted above).
        (TransientBackoff backoff, _) = Make(probeInterval: TimeSpan.Zero, pollOnly: true);

        TimeSpan delay = backoff.NextDelay(resetHint: null);

        Assert.Equal(TransientBackoff.MinProbeInterval, delay);
        Assert.True(delay > TimeSpan.Zero, "a zero probe interval would busy-loop the provider");
    }

    [Fact]
    public async Task ThePollHorizonSpendsTheProviderBound_NotTheExponentialBudget()
    {
        // The two horizons bound categorically different waits, which is why they are two config keys. Here
        // the exponential budget is a minute and the provider bound is four hours: on the poll horizon the
        // minute must not be what stops it, or an overnight wait would end after 60 seconds.
        (TransientBackoff backoff, List<TimeSpan> waited) = Make(
            budget: TimeSpan.FromMinutes(1),
            probeInterval: TimeSpan.FromMinutes(30),
            providerWaitBound: TimeSpan.FromHours(4),
            pollOnly: true);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(backoff.CanPauseAgain(), $"probe {i + 1} must still fit the provider bound");
            await backoff.PauseAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(TransientHorizon.Poll, backoff.LastHorizon);
        Assert.Equal(4, backoff.PauseCount);
        Assert.Equal(TimeSpan.FromHours(2), backoff.Elapsed);
        Assert.All(waited, w => Assert.Equal(TimeSpan.FromMinutes(30), w));
    }

    [Fact]
    public async Task APOLLWaitDoesNotDrainTheEXPONENTIALBudget_soTheNextBlipStillWaits()
    {
        // The silent one, found by an Integration test that was asserting something else entirely.
        //
        // A quota window and a blip are bounded by different config keys, so the cumulative meter must not be
        // judged against whichever horizon the CURRENT pause happens to be on. Read that way, one 30-minute
        // poll spends 30 minutes of the meter, and the next pause — un-hinted, therefore exponential — is
        // clamped against a 30-minute exponential budget the poll has already eaten. The clamp returns
        // ZERO: the harness announces a pause, waits not at all, and hammers the provider it had just
        // decided to wait for. Observed as "expected 00:00:06, actual 00:30:00" — the second wait silently
        // gone, and nothing in the run saying so.
        //
        // The rule is that the bound LATCHES: once a poll wait has been taken, this unit of work is in the
        // long-horizon world and maxProviderWaitHours governs from then on.
        (TransientBackoff backoff, List<TimeSpan> waited) = Make(
            budget: TimeSpan.FromMinutes(30),          // exactly consumed by the poll below, if shared
            probeInterval: TimeSpan.FromMinutes(30),
            providerWaitBound: TimeSpan.FromHours(12));

        await backoff.PauseAsync(TestContext.Current.CancellationToken, "8:30pm");   // hinted → poll
        Assert.Equal(TransientHorizon.Poll, backoff.LastHorizon);
        Assert.Equal(TimeSpan.FromMinutes(30), waited[0]);

        Assert.True(backoff.CanPauseAgain(), "the poll bound, not the exponential one, governs from here");
        await backoff.PauseAsync(TestContext.Current.CancellationToken);             // un-hinted → exponential

        Assert.Equal(TransientHorizon.Exponential, backoff.LastHorizon);
        Assert.True(
            waited[1] > TimeSpan.Zero,
            "the exponential pause was clamped to zero — a pause that does not wait is a retry storm "
            + "wearing a pause's name");
        Assert.Equal(TransientBackoff.BaseDelay * 2, waited[1]);
    }

    [Fact]
    public async Task ThePollHorizonIsBOUNDED_soARevokedKeyCannotHangTheRunForever()
    {
        // The bound is why waiting is safe to make the default. A permanently revoked key looks exactly like
        // a quota limit that has not cleared yet, and the only thing separating "wait it out" from "hang
        // forever" is that this eventually returns false and the caller halts with the honest reason.
        (TransientBackoff backoff, _) = Make(
            probeInterval: TimeSpan.FromMinutes(30),
            providerWaitBound: TimeSpan.FromHours(1),
            pollOnly: true);

        await backoff.PauseAsync(TestContext.Current.CancellationToken);
        Assert.True(backoff.CanPauseAgain());
        await backoff.PauseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromHours(1), backoff.Elapsed);
        Assert.False(backoff.CanPauseAgain());
    }

    [Fact]
    public async Task TheFinalWaitIsClampedToTheRemainingBound_NeverOvershooting()
    {
        // 45 minutes of a 1-hour bound already spent: the next probe must be the 15 minutes that remain, not
        // a full interval that would carry the wait past the bound the operator configured.
        (TransientBackoff backoff, _) = Make(
            probeInterval: TimeSpan.FromMinutes(30),
            providerWaitBound: TimeSpan.FromMinutes(45),
            pollOnly: true);

        TimeSpan first = backoff.NextDelay();
        Assert.Equal(TimeSpan.FromMinutes(30), first);

        // Spend it, then ask again.
        await backoff.PauseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMinutes(15), backoff.NextDelay());
    }
}
