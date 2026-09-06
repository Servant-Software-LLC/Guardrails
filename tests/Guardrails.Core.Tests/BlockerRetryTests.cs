using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;
// The class-(b) EXECUTOR is Guardrails.Core.Execution.BlockerRetry (imported above); the CEILINGS come
// from the shipped autonomy-dial config record Guardrails.Core.Model.BlockerRetry — same short name, other
// namespace — so it is aliased instead of importing Guardrails.Core.Model wholesale (which would collide).
using BlockerRetryConfig = Guardrails.Core.Model.BlockerRetry;
using RunConfig = Guardrails.Core.Model.RunConfig;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD-red tests for the class-(b) blocker bounded wait/backoff (issue #361 Phase 3, doc 12 §4.2). The
/// bounded wait REUSES the shipped transient discipline — the exponential backoff of
/// <see cref="TransientBackoff"/> and the cumulative <c>transientPauseBudgetSeconds</c> floor
/// (<see cref="RunConfig.TransientPauseBudgetSeconds"/>) — and bounds it further with the shipped autonomy
/// ceilings <see cref="BlockerRetryConfig.MaxAttempts"/> / <see cref="BlockerRetryConfig.TotalWaitSeconds"/>.
/// The wait is injected so these tests are sleep-free and deterministic. Every test drives the (currently
/// throwing) <see cref="BlockerRetry.RunAsync"/> — they FAIL until the loop is implemented.
/// </summary>
public sealed class BlockerRetryTests
{
    /// <summary>The shipped default cumulative pause budget (14400s), sourced from the real config record.</summary>
    private static TimeSpan ShippedTransientBudget =>
        TimeSpan.FromSeconds(new RunConfig { Version = 1 }.TransientPauseBudgetSeconds);

    /// <summary>
    /// A <see cref="BlockerRetry"/> whose injected delay records each requested wait and returns immediately
    /// (no real sleep), mirroring the <see cref="TransientBackoff"/> test seam.
    /// </summary>
    private static (BlockerRetry retry, List<TimeSpan> waited) Make(
        int maxAttempts, int totalWaitSeconds, TimeSpan transientPauseBudget)
    {
        var waited = new List<TimeSpan>();
        var retry = new BlockerRetry(
            new BlockerRetryConfig { MaxAttempts = maxAttempts, TotalWaitSeconds = totalWaitSeconds },
            transientPauseBudget,
            (d, ct) => { ct.ThrowIfCancellationRequested(); waited.Add(d); return Task.CompletedTask; });
        return (retry, waited);
    }

    /// <summary>
    /// Issue #616 — the run's token REACHES the wait.
    ///
    /// <para>
    /// The seam used to be <c>Func&lt;TimeSpan, Task&gt;</c> with no token at all, so a blocker-retry pause
    /// in flight when Ctrl-C landed ran to completion — bounded only by the autonomy dial's
    /// <c>totalWaitSeconds</c>, which is minutes. #603 established that the whole cancelled unwind must fit
    /// a 15 s ceiling; this was not a budget that was raised, it was one that was never counted, and it
    /// exceeded the ceiling by two orders of magnitude.
    /// </para>
    ///
    /// <para>
    /// Asserted on the token the seam is HANDED, not on how long anything took. That is the defect
    /// exactly: the token existed, reached the boundary, and was dropped — at the exponential-backoff call
    /// site by an adapter written <c>(d, _) =&gt; _delay(d)</c>, which discarded a token it had been given
    /// and made the omission look deliberate. A test that merely cancelled and watched the loop exit would
    /// pass without the fix, because <c>RunAsync</c> already re-checks the token at the top of each
    /// iteration — it would never enter the wait at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRunsToken_IsHandedToEveryWait_NotDropped()
    {
        var seen = new List<CancellationToken>();
        var retry = new BlockerRetry(
            new BlockerRetryConfig { MaxAttempts = 4, TotalWaitSeconds = 600 },
            TimeSpan.FromSeconds(600),
            (_, ct) => { seen.Add(ct); return Task.CompletedTask; });

        using var cts = new CancellationTokenSource();

        // Never clears, so it exhausts maxAttempts — exercising BOTH wait paths: the first-pause reset-hint
        // override and the exponential TransientBackoff behind it.
        BlockerRetryResult result = await retry.RunAsync(
            hasCleared: _ => false,
            resetHint: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token);

        Assert.Equal(BlockerRetryOutcome.Escalate, result.Outcome);
        Assert.NotEmpty(seen);
        Assert.All(seen, token => Assert.Equal(cts.Token, token));
    }

    /// <summary>
    /// The second half, and the one the issue says to decide rather than inherit: a cancelled wait
    /// ABANDONS the retry. It does not fall through into another attempt, which would be worse than the
    /// wait it replaced.
    ///
    /// <para>
    /// The fake blocks until cancelled — a wait that is genuinely in flight when the operator's Ctrl-C
    /// lands, which is the situation the issue describes. <c>hasCleared</c> having run exactly once proves
    /// the loop ended there rather than starting another attempt.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACancelledWait_AbandonsTheRetry_RatherThanRetryingImmediately()
    {
        using var cts = new CancellationTokenSource();
        int probes = 0;

        var retry = new BlockerRetry(
            new BlockerRetryConfig { MaxAttempts = 5, TotalWaitSeconds = 600 },
            TimeSpan.FromSeconds(600),
            async (_, ct) =>
            {
                cts.Cancel();                                   // the Ctrl-C, mid-wait
                await Task.Delay(Timeout.Infinite, ct);         // only ends by cancellation
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.RunAsync(
            hasCleared: _ => { probes++; return false; },
            resetHint: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token));

        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task ResolvesWithinCeiling_ReturnsResolved_AndRecordsLedger()
    {
        // Shipped defaults: maxAttempts 5, totalWaitSeconds 900; the 14400s transient budget never floors here.
        (BlockerRetry retry, List<TimeSpan> waited) =
            Make(maxAttempts: 5, totalWaitSeconds: 900, transientPauseBudget: ShippedTransientBudget);

        // The transient clears on the 3rd re-run (K = 3 < maxAttempts, cumulative wait well under 900s).
        BlockerRetryResult result = await retry.RunAsync(
            attempt => attempt >= 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BlockerRetryOutcome.Resolved, result.Outcome);
        Assert.Equal(3, result.Ledger.Attempts);
        // Reuses the shipped exponential schedule (2s, 4s, …): two backoffs before the 3rd re-run clears.
        Assert.Equal(new List<TimeSpan> { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, waited);
        Assert.Equal(TimeSpan.FromSeconds(6), result.Ledger.CumulativeWait);
    }

    [Fact]
    public async Task MaxAttemptsCeiling_Escalates_CarryingLedger()
    {
        (BlockerRetry retry, List<TimeSpan> waited) =
            Make(maxAttempts: 3, totalWaitSeconds: 900, transientPauseBudget: ShippedTransientBudget);

        // Never clears → the maxAttempts ceiling (3) trips long before the 900s wait ceiling.
        BlockerRetryResult result = await retry.RunAsync(
            _ => false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BlockerRetryOutcome.Escalate, result.Outcome);
        Assert.Equal(3, result.Ledger.Attempts);                       // reached maxAttempts
        // Two backoffs between the three attempts; the ledger the escalation carries records the wait.
        Assert.Equal(new List<TimeSpan> { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, waited);
        Assert.Equal(TimeSpan.FromSeconds(6), result.Ledger.CumulativeWait);
    }

    [Fact]
    public async Task TotalWaitSecondsCeiling_Escalates_WhenWaitTripsBeforeAttempts()
    {
        const int maxAttempts = 100;   // deliberately generous so the WAIT ceiling is what trips
        const int totalWaitSeconds = 10;
        (BlockerRetry retry, _) =
            Make(maxAttempts, totalWaitSeconds, transientPauseBudget: ShippedTransientBudget);

        BlockerRetryResult result = await retry.RunAsync(
            _ => false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BlockerRetryOutcome.Escalate, result.Outcome);
        // The cumulative-wait ceiling tripped FIRST — far below the 100-attempt ceiling (whichever first).
        Assert.True(
            result.Ledger.Attempts < maxAttempts,
            $"expected the wait ceiling to trip before {maxAttempts} attempts, but Attempts was {result.Ledger.Attempts}");
        Assert.True(
            result.Ledger.CumulativeWait <= TimeSpan.FromSeconds(totalWaitSeconds),
            $"cumulative wait {result.Ledger.CumulativeWait} must not exceed the {totalWaitSeconds}s ceiling");
    }

    [Fact]
    public async Task EffectiveBound_IsFlooredBy_TransientPauseBudget()
    {
        // doc 12 §4.2: the blocker ceiling does not exceed the shipped transient-pause budget floor. Here the
        // transient budget (10s) is SMALLER than the blocker's own 900s totalWaitSeconds, so it floors the
        // effective wall-clock bound.
        const int totalWaitSeconds = 900;
        var transientBudget = TimeSpan.FromSeconds(10);
        (BlockerRetry retry, _) =
            Make(maxAttempts: 100, totalWaitSeconds, transientPauseBudget: transientBudget);

        BlockerRetryResult result = await retry.RunAsync(
            _ => false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BlockerRetryOutcome.Escalate, result.Outcome);
        Assert.True(
            result.Ledger.CumulativeWait <= transientBudget,
            $"cumulative wait {result.Ledger.CumulativeWait} must be floored by the {transientBudget} transient budget");
        // …and thus far below the blocker's own 900s ceiling — proving the two compose (the floor wins).
        Assert.True(
            result.Ledger.CumulativeWait < TimeSpan.FromSeconds(totalWaitSeconds),
            $"the transient budget floor must clamp the effective bound below the {totalWaitSeconds}s blocker ceiling");
    }

    [Fact]
    public async Task Transient_DoesNotConsume_LogicRetryBudget()
    {
        // A transient is not a logic failure — even when it escalates to class (c), the task's retry budget
        // is untouched (the shipped transient-pause rule, doc 12 §4.2).
        (BlockerRetry retry, _) =
            Make(maxAttempts: 2, totalWaitSeconds: 900, transientPauseBudget: ShippedTransientBudget);

        BlockerRetryResult result = await retry.RunAsync(
            _ => false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(
            result.Ledger.ConsumedLogicRetry,
            "a class-(b) blocker must never decrement the task's logic-retry counter");
    }

    [Fact]
    public async Task Backoff_HonorsParsedResetHint_WhenPresent()
    {
        // Anchor on the shipped signal: the reset hint is parsed by ClaudeSignalClassifier.ExtractResetHint.
        string? hint = ClaudeSignalClassifier.ExtractResetHint(
            "You've hit your session limit · resets 11:20am (America/Chicago)");
        Assert.NotNull(hint);

        (BlockerRetry retry, List<TimeSpan> waited) =
            Make(maxAttempts: 5, totalWaitSeconds: 900, transientPauseBudget: ShippedTransientBudget);

        // The parsed reset hint (wait until the limit resets) overrides the exponential schedule.
        var resetWait = TimeSpan.FromSeconds(120);
        BlockerRetryResult result = await retry.RunAsync(
            attempt => attempt >= 2,
            resetHint: resetWait,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BlockerRetryOutcome.Resolved, result.Outcome);
        // The first backoff honors the reset hint instead of the exponential 2s.
        Assert.Equal(resetWait, waited[0]);
    }
}
