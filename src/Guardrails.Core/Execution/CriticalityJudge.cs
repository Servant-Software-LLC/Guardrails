using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// The criticality-assessment decider (issue #361 Phase 3, doc 12 §3.2/§3.3, §4.3, §5.2). For a class-(a)
/// JUDGMENT CALL — an agent-emitted <c>{"needsHuman": "…"}</c> question or a JIT wave checkpoint (§4.1) — it
/// runs an ADVISORY assessment through the reserved read-only <c>overwatch</c> prompt profile
/// (<see cref="IPromptRunner"/>, <see cref="SchedulerFactory"/>'s <c>overwatch</c> profile, EXACTLY as the
/// overwatcher's diagnose does — doc 12 §10 H) and DECIDES whether to escalate or proceed on a recorded
/// best-guess: <c>escalate ⟺ assessedCriticality ≥ escalationThreshold</c> (§3.3), honoring a per-gate
/// <see cref="GateThresholds"/> override over the run-wide dial (§3.5).
///
/// <para>It DECIDES ONLY. It never calls the escalation sink, injects the best-guess into the next attempt,
/// or writes the forensic trail — the wiring task (a later task) does that with the returned
/// <see cref="CriticalityDecision"/>. The decider enforces the design's hard invariants:</para>
/// <list type="number">
///   <item>A malformed / absent / errored assessment ⇒ <see cref="CriticalityOutcome.Escalate"/> (invariant 1,
///   §4.3): the judge is NEVER the verdict authority; the safe default is escalate, never spin.</item>
///   <item>Under <c>review-gate: proceed-unreviewed</c> (<see cref="GateThresholds.ReviewGate"/> ==
///   <see cref="ReviewGateDecision.ProceedUnreviewed"/>) an assessed <c>high</c>/<c>critical</c> ALWAYS
///   escalates — overriding the run-wide dial AND any per-gate override (the clamp, §5.2 / Blocker 1). A
///   <c>low</c>/<c>moderate</c> call is unaffected.</item>
///   <item>An UNKNOWN-failure widening (<see cref="CriticalityGate.UnknownFailure"/>, §4.3) may reclassify an
///   ambiguous failure to retryable ONLY up to <see cref="AutonomyConfig.MaxJudgeWidenings"/> times PER RUN
///   (tracked in the injected <see cref="WideningLedger"/>); once spent, a further unknown failure escalates
///   deterministically. The recorded widening rationale is advisory self-report, not an independent check.</item>
/// </list>
///
/// <para><b>STUB (TDD red).</b> <see cref="AssessAsync"/> THROWS so the tests in
/// <c>CriticalityAssessmentTests</c> compile but fail; the real assessment / threshold / clamp / widening
/// logic is authored by the implement task. Do NOT implement it here.</para>
/// </summary>
public sealed class CriticalityJudge
{
    private readonly IPromptRunner _runner;
    private readonly AutonomyConfig _config;
    private readonly WideningLedger _widenings;

    /// <param name="runner">
    /// The runner for the advisory assessment prompt — the reserved read-only <c>overwatch</c> profile
    /// (<see cref="SchedulerFactory"/>). In tests this is a FAKE returning a canned
    /// <see cref="PromptResult"/>; the decider NEVER makes a real prompt call under test.
    /// </param>
    /// <param name="config">The criticality dial in force for this run (§3.3–§3.5, §4.3).</param>
    /// <param name="widenings">The run-level widening ledger (§4.3); a fresh, empty ledger when null.</param>
    public CriticalityJudge(IPromptRunner runner, AutonomyConfig config, WideningLedger? widenings = null)
    {
        _runner = runner;
        _config = config;
        _widenings = widenings ?? new WideningLedger();
    }

    /// <summary>
    /// Run the advisory assessment for <paramref name="context"/> and DECIDE (escalate | proceed-best-guess),
    /// returning the full <see cref="CriticalityDecision"/> (criticality, confidence, best-guess, rationale).
    ///
    /// <para>Assessment wire shape (the reserved <c>overwatch</c> profile returns it as
    /// <see cref="PromptResult.ResultText"/>): a judgment call —
    /// <c>{ "criticality": "low|moderate|high|critical", "confidence": "low|moderate|high",
    /// "bestGuess": "…", "rationale": "…" }</c>; an unknown-failure widening
    /// (<see cref="CriticalityGate.UnknownFailure"/>) — <c>{ "retryable": true|false, "rationale": "…" }</c>.</para>
    /// </summary>
    public Task<CriticalityDecision> AssessAsync(CriticalityGateContext context, CancellationToken cancellationToken) =>
        throw new NotImplementedException(
            "CriticalityJudge.AssessAsync is not yet implemented (TDD red — doc 12 §3.2/§3.3, §4.3, §5.2).");
}

/// <summary>
/// The gate kinds the criticality decider assesses (doc 12 §4.1). <see cref="NeedsHuman"/> and
/// <see cref="WaveCheckpoint"/> are class-(a) judgment calls governed by the dial; <see cref="UnknownFailure"/>
/// is an ambiguous hard failure the judge may only ever WIDEN to retryable, bounded per run (§4.3).
/// </summary>
public enum CriticalityGate
{
    /// <summary>An agent-emitted <c>{"needsHuman": "…"}</c> question (§4.1); per-gate override <see cref="GateThresholds.NeedsHuman"/>.</summary>
    NeedsHuman,

    /// <summary>The JIT between-wave checkpoint, next wave unauthored (§4.1); per-gate override <see cref="GateThresholds.WaveCheckpoint"/>.</summary>
    WaveCheckpoint,

    /// <summary>An UNKNOWN/ambiguous failure the judge may widen to retryable, bounded by <see cref="AutonomyConfig.MaxJudgeWidenings"/> (§4.3).</summary>
    UnknownFailure
}

/// <summary>The decider's two terminal outcomes (doc 12 §4): halt-and-escalate, or proceed on a recorded best-guess.</summary>
public enum CriticalityOutcome
{
    /// <summary>Escalate: criticality ≥ threshold, a clamp, a spent widening cap, or a malformed assessment (the safe default).</summary>
    Escalate,

    /// <summary>Proceed with the recorded best-guess: criticality &lt; threshold (judgment call), or a sanctioned widening (§4.3).</summary>
    ProceedBestGuess
}

/// <summary>The judge's self-reported confidence in its assessment (doc 12 §6.2 <c>confidence</c>).</summary>
public enum JudgeConfidence
{
    /// <summary>Low confidence.</summary>
    Low,

    /// <summary>Moderate confidence.</summary>
    Moderate,

    /// <summary>High confidence.</summary>
    High
}

/// <summary>
/// The gate context handed to <see cref="CriticalityJudge.AssessAsync"/> — which gate is being decided (so the
/// per-gate <see cref="GateThresholds"/> override resolves) plus the human-readable detail the assessment
/// prompt reasons over (the <c>needsHuman</c> question, the checkpoint description, or the failure summary).
/// </summary>
public sealed record CriticalityGateContext
{
    /// <summary>Which gate this is (selects the per-gate threshold override, §3.5).</summary>
    public required CriticalityGate Gate { get; init; }

    /// <summary>The question / checkpoint description / failure summary the assessment reasons over.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// The decider's result (doc 12 §4/§6.2) — advisory input to the wiring task, never itself an action. Carries
/// the outcome plus the assessed <see cref="Criticality"/> / <see cref="Confidence"/> / <see cref="BestGuess"/>
/// / <see cref="Rationale"/> that land in the <c>decisions[]</c> + <c>autonomy.jsonl</c> forensic trail.
/// </summary>
public sealed record CriticalityDecision
{
    /// <summary>Escalate or proceed-with-best-guess (§4).</summary>
    public required CriticalityOutcome Outcome { get; init; }

    /// <summary>
    /// The assessed criticality level (reusing the <see cref="EscalationThreshold"/> scale — <c>Low</c> …
    /// <c>Critical</c> — since the dial IS "the lowest criticality that still escalates", §3.3). Null when the
    /// assessment was malformed/absent (invariant 1) or the gate is a hard-blocker with no criticality (§6.2).
    /// </summary>
    public EscalationThreshold? Criticality { get; init; }

    /// <summary>The judge's self-reported confidence; null when no valid assessment was parsed (§6.2).</summary>
    public JudgeConfidence? Confidence { get; init; }

    /// <summary>The recorded best-guess taken when <see cref="Outcome"/> is <see cref="CriticalityOutcome.ProceedBestGuess"/>; null otherwise (§6.2).</summary>
    public string? BestGuess { get; init; }

    /// <summary>The judge's rationale (for a widening, the advisory "why retryable" self-report, §4.3); null when unavailable.</summary>
    public string? Rationale { get; init; }

    /// <summary>True when this decision WIDENED an unknown failure to retryable (§4.3) — the act that counts against <see cref="AutonomyConfig.MaxJudgeWidenings"/>.</summary>
    public bool Widened { get; init; }
}

/// <summary>
/// The run-level widening ledger (doc 12 §4.3): the counter that bounds how many times a judge may reclassify
/// an UNKNOWN failure as retryable across the WHOLE run. It is threaded/injected (one per run) so the cap is
/// enforced run-wide, not per gate — defeating the abuse mode where an over-eager judge marks every gate
/// transient and spins to the ceiling. Once <see cref="Count"/> reaches
/// <see cref="AutonomyConfig.MaxJudgeWidenings"/>, further unknown failures escalate deterministically.
/// </summary>
public sealed class WideningLedger
{
    /// <param name="count">The already-spent widening count (0 for a fresh run; a test may preload it to the cap).</param>
    public WideningLedger(int count = 0) => Count = count;

    /// <summary>How many unknown-failure widenings have been spent this run.</summary>
    public int Count { get; private set; }

    /// <summary>Record one widening (advisory self-report still counts against the cap, §4.3).</summary>
    public void RecordWidening() => Count++;
}
