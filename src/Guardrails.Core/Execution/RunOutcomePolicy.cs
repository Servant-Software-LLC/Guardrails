namespace Guardrails.Core.Execution;

/// <summary>
/// The PURE end-of-run policy over a run's recorded <see cref="DecisionEntry"/> stream (issue #361 Phase 4,
/// doc 12 §1 "delivery interacts with best-guessing" + §5.2 Option P / §7.1). It runs no prompt and touches
/// no disk — it is a pure function of the <c>decisions[]</c> the run already recorded, so it is the ideal
/// unit-test base and the single place two run-outcome facts are derived:
///
/// <list type="bullet">
///   <item><b>Delivery suppression (the §1 hard rule, #340):</b> a run that recorded even one decision the
///   shared delivery-interlock predicate holds — <see cref="SuppressingDecision"/> names it and returns the
///   evidence — shaped its result with machine-decided work, so <c>mergeOnSuccess</c> DEFAULTS to OFF — the
///   verified work stays on the plan branch and is never auto-delivered.</item>
///   <item><b>The unreviewed-wave flag (§5.2 Option P / §7.1):</b> the number of
///   <see cref="DecisionTokens.ProceededUnreviewed"/> decisions is the "ran with N unreviewed waves" flag
///   that drives the distinct non-zero exit code so an automated firstmate consumer can never read the run
///   as clean green.</item>
/// </list>
///
/// <para>These members are the SINGLE source of the delivery-suppression verdict and of the evidence for
/// it: <c>SuppressesDelivery</c> is defined in terms of <c>SuppressingDecision</c>, so the answer and the
/// decision it rests on cannot drift apart. That coupling is deliberate - a banner, a journal record and
/// an exit code that each re-derived "was delivery suppressed?" independently is how one of them ends up
/// naming a cause the others do not agree with (#597, where the banner blamed a config key for a
/// suppression a recorded decision had caused).</para>
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
        SuppressingDecision(decisions) is not null;

    /// <summary>
    /// The FIRST decision that suppresses delivery, or <c>null</c> when none does — the same predicate as
    /// <see cref="SuppressesDelivery"/>, spelled once, but returning the EVIDENCE rather than a bare bool.
    /// <para>
    /// <b>Why the entry and not a boolean (issue #597).</b> The end-of-run "work not delivered" banner used
    /// to name <c>mergeOnSuccess</c> as the cause for every suppression, including this one — where
    /// <c>mergeOnSuccess</c> is ON and the real cause is the autonomous-mode interlock. An operator who
    /// checked <c>guardrails.json</c> (no key), then the default in source (<c>true</c>), then whether the
    /// default had changed (it had not), still had not found the cause; someone without source access could
    /// not get past the first step. The banner and the durable <c>delivery.reason</c> can only name the
    /// cause — <i>which</i> decision, at <i>which</i> task — if the policy hands them the entry, so the
    /// operator can judge whether the machine's guess is stale before deciding to deliver.
    /// </para>
    /// </summary>
    /// <param name="decisions">The run's recorded <c>decisions[]</c> stream.</param>
    /// <returns>The first <c>proceeded-best-guess</c> / <c>proceeded-unreviewed</c> entry, else null.</returns>
    public static DecisionEntry? SuppressingDecision(IEnumerable<DecisionEntry> decisions) =>
        decisions.FirstOrDefault(HoldsDelivery);

    /// <summary>
    /// THE delivery-interlock token set, spelled ONCE (design 41 §6, fact 11). Both
    /// <see cref="SuppressingDecision"/> (run end) and <see cref="SuppressingDecisionForDelivery"/> (every wave
    /// barrier) are defined in terms of it, so a new suppressing token cannot be added to one spelling and
    /// missed by the other.
    /// </summary>
    private static bool HoldsDelivery(DecisionEntry decision) =>
        decision.Decision == DecisionTokens.ProceededBestGuess ||
        decision.Decision == DecisionTokens.ProceededUnreviewed ||
        decision.Decision == DecisionTokens.AutoSupplied;

    /// <summary>
    /// The delivery-scoped counterpart of <see cref="SuppressingDecision"/> (design 39 §1a/§1b, review round
    /// 4 <c>d39-interlock-ride-along</c>): a delivery carries every wave since the previous delivery point
    /// (this one included), so the check takes the SET of waves the delivery covers rather than one wave or
    /// the whole run.
    /// <para>
    /// Returns the FIRST <see cref="DecisionTokens.ProceededBestGuess"/> / <see cref="DecisionTokens.ProceededUnreviewed"/>
    /// decision whose <see cref="DecisionEntry.Wave"/> is one of <paramref name="coveredWaves"/> — or whose
    /// <see cref="DecisionEntry.Wave"/> is <c>null</c>, because a suppressing decision recorded outside any
    /// wave holds EVERY delivery (the check fails closed rather than leaking one past an unattributed
    /// decision) — else <c>null</c>.
    /// </para>
    /// <para>
    /// NOT a replacement for <see cref="SuppressingDecision"/>: the run-end interlock (<c>Finalize</c>) stays
    /// on the single-argument, run-scoped pair. This one exists for the barrier delivery, whose covered-wave
    /// set task 08 supplies.
    /// </para>
    /// </summary>
    /// <param name="decisions">The run's recorded <c>decisions[]</c> stream.</param>
    /// <param name="coveredWaves">The waves this delivery carries (every wave since the previous delivery point, inclusive).</param>
    /// <returns>The first suppressing entry whose wave is covered (or unattributed), else null.</returns>
    public static DecisionEntry? SuppressingDecisionForDelivery(
        IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
        decisions.FirstOrDefault(d => HoldsDelivery(d) && (d.Wave is null || coveredWaves.Contains(d.Wave)));

    /// <summary>
    /// True when <see cref="SuppressingDecisionForDelivery"/> finds a decision that holds this delivery —
    /// defined in terms of the entry, exactly like <see cref="SuppressesDelivery(IEnumerable{DecisionEntry})"/>,
    /// so the verdict and its evidence cannot drift apart (#597).
    /// </summary>
    /// <param name="decisions">The run's recorded <c>decisions[]</c> stream.</param>
    /// <param name="coveredWaves">The waves this delivery carries (every wave since the previous delivery point, inclusive).</param>
    /// <returns>Whether this delivery must be held.</returns>
    public static bool SuppressesDelivery(
        IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
        SuppressingDecisionForDelivery(decisions, coveredWaves) is not null;

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
