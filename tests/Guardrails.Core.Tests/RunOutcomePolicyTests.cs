using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the PURE end-of-run <see cref="RunOutcomePolicy"/> (issue #361 Phase 4, doc 12 §1 "delivery
/// interacts with best-guessing" + §5.2 Option P / §7.1). The policy is a pure function over a run's
/// recorded <see cref="DecisionEntry"/> stream — it runs no prompt and touches no disk — so these are
/// trivially re-runnable unit tests. Two facts are load-bearing:
///
/// <list type="bullet">
///   <item><b>Delivery suppression (the §1 hard rule, #340):</b> a run that recorded even one
///   <see cref="DecisionTokens.ProceededBestGuess"/> or <see cref="DecisionTokens.ProceededUnreviewed"/>
///   decision defaults <c>mergeOnSuccess</c> to OFF — machine-decided work is never auto-delivered. The
///   load-bearing NEGATIVE: a run whose only decisions are ordinary (escalate / halt / none) delivers
///   normally.</item>
///   <item><b>The unreviewed-wave count (§5.2 Option P / §7.1):</b> the count of
///   <see cref="DecisionTokens.ProceededUnreviewed"/> decisions is the "ran with N unreviewed waves" flag +
///   distinct-exit trigger — and a best-guess is NOT that case (suppresses delivery, count still 0).</item>
/// </list>
///
/// <para>These are TDD-red tests: they COMPILE against the throwing stub and FAIL until the policy is
/// implemented by a later wave-04 task.</para>
/// </summary>
public sealed class RunOutcomePolicyTests
{
    // Required-member helper: a DecisionEntry carrying just the decision token under test. Mirrors the real
    // shipped shape (Boundary/Policy/Decision/Subject/Headline are all required members of DecisionEntry).
    private static DecisionEntry Decision(string token, string subject = "wave-04-review-gate-policy") => new()
    {
        Boundary = "wave",
        Policy = "auto",
        Decision = token,
        Subject = subject,
        Headline = $"recorded decision: {token}"
    };

    // ── Delivery suppression: the §1 hard rule (#340) ─────────────────────────────────────────────

    [Fact]
    public void SuppressesDelivery_True_WhenProceededBestGuessRecorded()
    {
        // §1 hard rule: a best-guess shaped the result ⇒ machine-decided work is never auto-delivered
        // (mergeOnSuccess OFF). Ordinary decisions alongside it must not mask the best-guess.
        var decisions = new[]
        {
            Decision(DecisionTokens.Escalated),
            Decision(DecisionTokens.ProceededBestGuess),
            Decision(DecisionTokens.Halted)
        };

        Assert.True(RunOutcomePolicy.SuppressesDelivery(decisions));
    }

    [Fact]
    public void SuppressesDelivery_True_WhenProceededUnreviewedRecorded()
    {
        // §5.2 Option P: an explicitly-unreviewed wave also defaults mergeOnSuccess OFF — the unreviewed
        // work stays on the plan branch, never auto-delivered.
        var decisions = new[]
        {
            Decision(DecisionTokens.Escalated),
            Decision(DecisionTokens.ProceededUnreviewed)
        };

        Assert.True(RunOutcomePolicy.SuppressesDelivery(decisions));
    }

    [Fact]
    public void SuppressesDelivery_False_WhenNoMachineDecisionRecorded()
    {
        // The load-bearing NEGATIVE (§1 / §7.1): a run whose only decisions are ordinary — escalate / halt,
        // or none at all — recorded no machine-decided best-guess/unreviewed work, so it DELIVERS normally.
        // Do NOT drop this case: without it a stub that always returns true would pass every positive test.
        Assert.False(RunOutcomePolicy.SuppressesDelivery(Array.Empty<DecisionEntry>()));

        var ordinary = new[]
        {
            Decision(DecisionTokens.Escalated),
            Decision(DecisionTokens.Halted),
            Decision(DecisionTokens.AutoApplied),
            Decision(DecisionTokens.BlockerRetried)
        };
        Assert.False(RunOutcomePolicy.SuppressesDelivery(ordinary));
    }

    // ── The unreviewed-wave count: §5.2 Option P / §7.1 distinct-exit trigger ──────────────────────

    [Fact]
    public void ProceededUnreviewedWaveCount_IsZero_WhenNoUnreviewedDecision()
    {
        // 0 when the run proceeded unreviewed nowhere — an escalate/halt-only run has no unreviewed waves.
        var ordinary = new[]
        {
            Decision(DecisionTokens.Escalated),
            Decision(DecisionTokens.Halted)
        };

        Assert.Equal(0, RunOutcomePolicy.ProceededUnreviewedWaveCount(ordinary));
    }

    [Fact]
    public void ProceededUnreviewedWaveCount_CountsProceededUnreviewedDecisions()
    {
        // N for N distinct unreviewed waves (the "ran with N unreviewed waves" flag) — counts ONLY the
        // proceeded-unreviewed decisions, ignoring the ordinary/best-guess ones interleaved with them.
        var decisions = new[]
        {
            Decision(DecisionTokens.ProceededUnreviewed, "wave-04-a"),
            Decision(DecisionTokens.Escalated),
            Decision(DecisionTokens.ProceededUnreviewed, "wave-05-b"),
            Decision(DecisionTokens.ProceededBestGuess),
            Decision(DecisionTokens.ProceededUnreviewed, "wave-06-c")
        };

        Assert.Equal(3, RunOutcomePolicy.ProceededUnreviewedWaveCount(decisions));
    }

    // ── Best-guess suppresses delivery but is NOT the distinct-exit case ───────────────────────────

    [Fact]
    public void BestGuessedButNotUnreviewed_SuppressesDelivery_AndCountIsZero()
    {
        // §1 + §7.1: a best-guess suppresses delivery (mergeOnSuccess OFF) yet is NOT an unreviewed wave, so
        // the distinct-exit count stays 0. Both facts hold at once over the same decisions[].
        var decisions = new[]
        {
            Decision(DecisionTokens.Escalated),
            Decision(DecisionTokens.ProceededBestGuess),
            Decision(DecisionTokens.Halted)
        };

        Assert.True(RunOutcomePolicy.SuppressesDelivery(decisions));
        Assert.Equal(0, RunOutcomePolicy.ProceededUnreviewedWaveCount(decisions));
    }
}
