using System.Text;
using System.Text.Json;
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
    ///
    /// <para>Invariant 1 (§4.3, verdict-from-files): the judge is NEVER the verdict authority. A thrown runner,
    /// an incomplete/errored result, or an unparseable body all yield <see cref="CriticalityOutcome.Escalate"/>
    /// — the safe default is escalate, never spin.</para>
    /// </summary>
    public async Task<CriticalityDecision> AssessAsync(CriticalityGateContext context, CancellationToken cancellationToken)
    {
        PromptResult result;
        try
        {
            PromptInvocation invocation = BuildInvocation(context);
            result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Advisory-never-gates: a thrown runner is an absent assessment ⇒ the safe default (invariant 1).
            return Escalate();
        }

        // The runner did not complete (or reported an error), or produced no result text ⇒ treat the
        // assessment as ABSENT and escalate — never trust a body the run did not stand behind (§4.3).
        if (!result.Completed || result.IsError || string.IsNullOrWhiteSpace(result.ResultText))
        {
            return Escalate();
        }

        return context.Gate == CriticalityGate.UnknownFailure
            ? DecideUnknownFailure(result.ResultText!)
            : DecideJudgmentCall(context.Gate, result.ResultText!);
    }

    // --- judgment call: threshold compare + proceed-unreviewed clamp (§3.3/§3.5, §5.2) ----------

    /// <summary>
    /// Decide a class-(a) judgment call (<see cref="CriticalityGate.NeedsHuman"/> /
    /// <see cref="CriticalityGate.WaveCheckpoint"/>): parse the advisory assessment, apply the
    /// <c>proceed-unreviewed</c> clamp, then compare against the effective (per-gate-overridden) threshold.
    /// </summary>
    private CriticalityDecision DecideJudgmentCall(CriticalityGate gate, string resultText)
    {
        if (!TryParseAssessment(resultText, out EscalationThreshold assessed, out JudgeConfidence? confidence,
            out string? bestGuess, out string? rationale))
        {
            // Invariant 1: nothing parseable ⇒ no assessed level, escalate (the safe default).
            return Escalate();
        }

        // The proceed-unreviewed clamp (§5.2 / Blocker 1): under `review-gate: proceed-unreviewed`, an assessed
        // `high`/`critical` ALWAYS escalates — overriding the run-wide dial AND every per-gate override. This is
        // the runtime mirror of the load-time PlanValidator.ViolatesCompoundConfig (GR2040) predicate: never
        // best-guess a hard call inside an unreviewed wave.
        if (_config.GateThresholds?.ReviewGate == ReviewGateDecision.ProceedUnreviewed
            && assessed >= EscalationThreshold.High)
        {
            return new CriticalityDecision
            {
                Outcome = CriticalityOutcome.Escalate,
                Criticality = assessed,
                Confidence = confidence,
                Rationale = rationale
            };
        }

        // The deterministic threshold compare (§3.3): escalate ⟺ assessedCriticality ≥ effectiveThreshold.
        if (assessed >= EffectiveThreshold(gate))
        {
            return new CriticalityDecision
            {
                Outcome = CriticalityOutcome.Escalate,
                Criticality = assessed,
                Confidence = confidence,
                Rationale = rationale
            };
        }

        // Below threshold ⇒ proceed on the recorded best-guess (carrying the best-guess text + rationale).
        return new CriticalityDecision
        {
            Outcome = CriticalityOutcome.ProceedBestGuess,
            Criticality = assessed,
            Confidence = confidence,
            BestGuess = bestGuess,
            Rationale = rationale
        };
    }

    /// <summary>
    /// The effective threshold for <paramref name="gate"/> (§3.5): a per-gate <see cref="GateThresholds"/>
    /// override for this gate when present, else the run-wide <see cref="AutonomyConfig.EscalationThreshold"/>.
    /// </summary>
    private EscalationThreshold EffectiveThreshold(CriticalityGate gate)
    {
        GateThresholds? gates = _config.GateThresholds;
        EscalationThreshold? perGate = gate switch
        {
            CriticalityGate.NeedsHuman => gates?.NeedsHuman,
            CriticalityGate.WaveCheckpoint => gates?.WaveCheckpoint,
            _ => null
        };
        return perGate ?? _config.EscalationThreshold;
    }

    // --- unknown failure: the maxJudgeWidenings run-level cap (§4.3) -----------------------------

    /// <summary>
    /// Decide an <see cref="CriticalityGate.UnknownFailure"/> (§4.3): the judge may only ever WIDEN an ambiguous
    /// failure to retryable, bounded by <see cref="AutonomyConfig.MaxJudgeWidenings"/> per run (the injected
    /// <see cref="WideningLedger"/>). A declined widening (or a spent cap) escalates deterministically and
    /// spends none of the cap — the judge can make the harness MORE patient, never less.
    /// </summary>
    private CriticalityDecision DecideUnknownFailure(string resultText)
    {
        if (!TryParseWidening(resultText, out bool retryable, out string? rationale))
        {
            // Invariant 1: a malformed widening self-report ⇒ escalate (the safe default).
            return Escalate();
        }

        // The judge declined to widen ⇒ escalate; spends no cap (it can only ever add patience, never remove it).
        if (!retryable)
        {
            return new CriticalityDecision { Outcome = CriticalityOutcome.Escalate, Rationale = rationale };
        }

        // Cap already spent ⇒ a further unknown failure escalates deterministically, even though the judge
        // still wants to widen — the run-level bound against an over-eager judge spinning every gate (§4.3).
        if (_widenings.Count >= _config.MaxJudgeWidenings)
        {
            return new CriticalityDecision { Outcome = CriticalityOutcome.Escalate, Rationale = rationale };
        }

        // Under the cap ⇒ WIDEN to retryable (proceed). The advisory self-report is recorded but STILL counts
        // against the run-level cap (the record is not an independent check).
        _widenings.RecordWidening();
        return new CriticalityDecision
        {
            Outcome = CriticalityOutcome.ProceedBestGuess,
            Rationale = rationale,
            Widened = true
        };
    }

    // --- assessment parsing --------------------------------------------------------------------

    /// <summary>
    /// Parse a judgment-call assessment body. Returns false (⇒ escalate, invariant 1) unless the body is a JSON
    /// object carrying a recognised <c>criticality</c> token (<see cref="EscalationThresholds.TryParse"/>);
    /// <c>confidence</c> / <c>bestGuess</c> / <c>rationale</c> are best-effort and null when absent/invalid.
    /// </summary>
    private static bool TryParseAssessment(string resultText, out EscalationThreshold criticality,
        out JudgeConfidence? confidence, out string? bestGuess, out string? rationale)
    {
        criticality = default;
        confidence = null;
        bestGuess = null;
        rationale = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(resultText);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("criticality", out JsonElement critEl)
                || critEl.ValueKind != JsonValueKind.String
                || !EscalationThresholds.TryParse(critEl.GetString() ?? "", out criticality))
            {
                return false; // no recognised criticality ⇒ not a usable assessment.
            }

            if (root.TryGetProperty("confidence", out JsonElement confEl)
                && confEl.ValueKind == JsonValueKind.String
                && TryParseConfidence(confEl.GetString(), out JudgeConfidence c))
            {
                confidence = c;
            }

            bestGuess = ReadString(root, "bestGuess");
            rationale = ReadString(root, "rationale");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parse an unknown-failure widening body (§4.3). Returns false (⇒ escalate) unless the body is a JSON
    /// object carrying a boolean <c>retryable</c> field; <c>rationale</c> is best-effort.
    /// </summary>
    private static bool TryParseWidening(string resultText, out bool retryable, out string? rationale)
    {
        retryable = false;
        rationale = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(resultText);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("retryable", out JsonElement retEl)
                || (retEl.ValueKind != JsonValueKind.True && retEl.ValueKind != JsonValueKind.False))
            {
                return false;
            }

            retryable = retEl.GetBoolean();
            rationale = ReadString(root, "rationale");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parse a <c>confidence</c> token (trim + case-insensitive): <c>low</c> / <c>moderate</c> / <c>high</c>.</summary>
    private static bool TryParseConfidence(string? value, out JudgeConfidence confidence)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "low":
                confidence = JudgeConfidence.Low;
                return true;
            case "moderate":
                confidence = JudgeConfidence.Moderate;
                return true;
            case "high":
                confidence = JudgeConfidence.High;
                return true;
            default:
                confidence = JudgeConfidence.Low;
                return false;
        }
    }

    /// <summary>The trimmed string value of <paramref name="name"/> on <paramref name="root"/>, or null when absent/non-string/blank.</summary>
    private static string? ReadString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            string? value = el.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    // --- the advisory assessment prompt --------------------------------------------------------

    /// <summary>The safe-default escalate decision — no assessed level, no best-guess, no widening (invariant 1).</summary>
    private static CriticalityDecision Escalate() => new() { Outcome = CriticalityOutcome.Escalate };

    /// <summary>
    /// Compose the <see cref="PromptInvocation"/> for the advisory assessment. The already-configured read-only
    /// <c>overwatch</c> runner (§10 H) is injected, so this carries only the constrained prompt + the
    /// diagnose-class runtime shape (bounded turns/timeout, no extra env); the paths are left to the
    /// composition-root's runner (the FAKE ignores the invocation under test).
    /// </summary>
    private PromptInvocation BuildInvocation(CriticalityGateContext context) => new()
    {
        ComposedPrompt = BuildAssessmentPrompt(context),
        WorkingDirectory = string.Empty,
        PlanDirectory = string.Empty,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal),
        Settings = new PromptRunnerSettings { MaxTurns = 10 },
        Timeout = TimeSpan.FromMinutes(5),
        StreamLogPath = string.Empty
    };

    /// <summary>
    /// The constrained assessment prompt: for a judgment call it asks for a criticality/confidence/best-guess
    /// self-report; for an unknown failure it asks the bounded retryable/not read (§4.3). The judge remains
    /// advisory — the harness, not this reply, decides via the deterministic threshold/clamp/cap.
    /// </summary>
    private static string BuildAssessmentPrompt(CriticalityGateContext context)
    {
        var sb = new StringBuilder();
        if (context.Gate == CriticalityGate.UnknownFailure)
        {
            sb.AppendLine("# Criticality assessment: unknown failure (advisory widening)");
            sb.AppendLine();
            sb.AppendLine($"Failure: {context.Detail}");
            sb.AppendLine();
            sb.AppendLine(
                "This failure was not recognised as a known-transient signal. Decide whether a bounded retry " +
                "can plausibly clear it (retryable) or it is structural (not retryable). The safe default is " +
                "NOT retryable — only widen when you have a concrete reason.");
            sb.AppendLine();
            sb.AppendLine("Return ONLY this JSON object:");
            sb.Append("""{"retryable":true|false,"rationale":"<why>"}""");
            return sb.ToString();
        }

        string gate = context.Gate == CriticalityGate.WaveCheckpoint ? "between-wave checkpoint" : "needs-human question";
        sb.AppendLine($"# Criticality assessment: {gate} (advisory)");
        sb.AppendLine();
        sb.AppendLine($"Decision to assess: {context.Detail}");
        sb.AppendLine();
        sb.AppendLine(
            "Assess how CRITICAL this judgment call is — how much damage a wrong best-guess would do if the " +
            "harness proceeds unattended instead of escalating to a human. Report your confidence and, when " +
            "the call is low enough to best-guess, the conservative default you would take and why.");
        sb.AppendLine();
        sb.AppendLine("Return ONLY this JSON object:");
        sb.Append(
            """{"criticality":"low|moderate|high|critical","confidence":"low|moderate|high","bestGuess":"<conservative default>","rationale":"<why>"}""");
        return sb.ToString();
    }
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
