namespace Guardrails.Core.Execution;

/// <summary>
/// The PURE end-of-run policy over a run's recorded <see cref="DecisionEntry"/> stream (issue #361 Phase 4,
/// doc 12 §1 "delivery interacts with best-guessing" + §5.2 Option P / §7.1). It runs no prompt and touches
/// no disk — it is a pure function of the <c>decisions[]</c> the run already recorded, so it is the ideal
/// unit-test base and the single place two run-outcome facts are derived:
///
/// <list type="bullet">
///   <item><b>Delivery suppression (the §1 hard rule, #340):</b> a run that recorded even one
///   <see cref="DecisionTokens.ProceededBestGuess"/> or <see cref="DecisionTokens.ProceededUnreviewed"/>
///   decision shaped its result with machine-decided work, so <c>mergeOnSuccess</c> DEFAULTS to OFF — the
///   verified work stays on the plan branch and is never auto-delivered.</item>
///   <item><b>The unreviewed-wave flag (§5.2 Option P / §7.1):</b> the number of
///   <see cref="DecisionTokens.ProceededUnreviewed"/> decisions is the "ran with N unreviewed waves" flag
///   that drives the distinct non-zero exit code so an automated firstmate consumer can never read the run
///   as clean green.</item>
/// </list>
///
/// <para>STUB (TDD red): both members throw so the authored tests COMPILE but FAIL until a later task
/// implements the real logic. Mirrors the throwing-stub convention used elsewhere in wave 3/4.</para>
/// </summary>
public static class RunOutcomePolicy
{
    /// <summary>
    /// True when the run recorded at least one <see cref="DecisionTokens.ProceededBestGuess"/> or
    /// <see cref="DecisionTokens.ProceededUnreviewed"/> decision — machine-decided work shaped the result, so
    /// delivery (<c>mergeOnSuccess</c>) DEFAULTS to OFF (doc 12 §1 hard rule, #340). False for a run whose only
    /// decisions are ordinary (escalate / halt) or that recorded none — such a run delivers normally.
    /// </summary>
    /// <param name="decisions">The run's recorded <c>decisions[]</c> stream.</param>
    /// <returns>Whether auto-delivery must be suppressed.</returns>
    public static bool SuppressesDelivery(IEnumerable<DecisionEntry> decisions) =>
        decisions.Any(d =>
            d.Decision == DecisionTokens.ProceededBestGuess ||
            d.Decision == DecisionTokens.ProceededUnreviewed);

    /// <summary>
    /// The number of <see cref="DecisionTokens.ProceededUnreviewed"/> decisions the run recorded (doc 12 §5.2
    /// Option P / §7.1) — the "ran with N unreviewed waves" flag and the distinct-exit trigger. Zero when the
    /// run proceeded unreviewed nowhere (including a run that best-guessed but was never unreviewed).
    /// </summary>
    /// <param name="decisions">The run's recorded <c>decisions[]</c> stream.</param>
    /// <returns>The count of <c>proceeded-unreviewed</c> decisions.</returns>
    public static int ProceededUnreviewedWaveCount(IEnumerable<DecisionEntry> decisions) =>
        decisions.Count(d => d.Decision == DecisionTokens.ProceededUnreviewed);
}
