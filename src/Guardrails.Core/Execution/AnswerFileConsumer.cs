namespace Guardrails.Core.Execution;

/// <summary>
/// Resume-time consumption of a firstmate <see cref="AnswerFile"/> (doc 12 §7.6, issue #361 Phase 3) — a
/// narrow, additive PRE-CHECK in front of the #190 outcome-agnostic reset. For a unit about to re-hit an
/// escalated gate, the consumer:
/// <list type="number">
///   <item><b>Finds</b> a pending <c>…&lt;seq&gt;-&lt;gate&gt;.answer.json</c> beside an escalation record whose
///     <c>status</c> is not yet <c>consumed</c>.</item>
///   <item><b>Validates the binding — ALL must hold, else REJECT + re-escalate:</b> <c>{runId, seq, gate,
///     subject}</c> echo the escalation VERBATIM (the monotonic <c>seq</c> makes the tuple unique, §7.1); the
///     gate is ANSWERABLE (<c>needs-human</c>/<c>wave-checkpoint</c> ONLY — never <c>review-gate</c> (§7.5, no
///     kind exists), never a clamped <c>high</c>/<c>critical</c> hard call under <c>proceed-unreviewed</c>
///     (§7.3 Blocker 1), never terminal); and <c>definitionHash</c> equals BOTH the escalation record's hash
///     AND the unit's CURRENT <c>TaskDefinitionHash</c>/<c>WaveDefinitionHash</c> (a STALE answer is rejected,
///     mirroring #274/§7.2).</item>
///   <item><b>Injects instead of re-escalating:</b> for <c>needs-human</c>, the answer <c>text</c> is injected
///     into the next attempt's composed prompt as clearly-delimited UNTRUSTED human-answer DATA (§7.4 Finding
///     4 — never a harness instruction, never able to reach the verdict surface); for <c>wave-checkpoint</c>,
///     the <c>proceed</c>/<c>hold</c> decision is applied. Records a <see cref="DecisionTokens.AnswerInjected"/>
///     decision (provenance + bound escalation id + matched hash) and flips the escalation <c>status</c> to
///     <c>consumed</c>, CAS-guarded so two concurrent resumes never double-inject (§7.1).</item>
///   <item><b>No / rejected answer ⇒ unchanged re-escalate</b> — graceful degrade to a plain forensic halt,
///     with the rejection reason recorded (§7.6).</item>
/// </list>
///
/// TDD-RED STUB (issue #361 Phase 3, task <c>12-author-tests-answer-consumption</c>): <see cref="Consume"/>
/// throws so the <c>AnswerFileConsumptionTests</c> security matrix COMPILES and FAILS. The real binding
/// validation, the dual-hash staleness check, the CAS once-only status flip, and the delimited-untrusted-data
/// injection land in the sibling implementation task (which also adds the OPTIONAL injection parameter to
/// <c>PromptComposer.ComposeAction</c>); this stub stays self-contained and touches no shipped type.
/// </summary>
public sealed class AnswerFileConsumer
{
    private readonly string _escalationsDir;

    /// <summary>
    /// Construct the consumer over the CREATING run's <c>escalations/</c> directory
    /// (<c>logs/&lt;runId&gt;/escalations/</c>). Consumption is anchored HERE regardless of the resume's own
    /// (new) <c>runId</c> — the <c>open → answered → consumed</c> <c>status</c> persists in the creating run's
    /// dir, the cross-<c>runId</c> bookkeeping that keeps a consumed escalation consumed across every later
    /// resume (§7.1/§7.6).
    /// </summary>
    public AnswerFileConsumer(string escalationsDir)
    {
        _escalationsDir = escalationsDir;
    }

    /// <summary>
    /// Attempt to consume the pending answer for the escalation <c>&lt;seq&gt;-&lt;gate&gt;</c>.
    /// <paramref name="currentDefinitionHash"/> is the unit's freshly-recomputed
    /// <c>TaskDefinitionHash</c>/<c>WaveDefinitionHash</c> (the anti-stale check compares the answer against
    /// it AND the escalation record's captured hash). <paramref name="proceedUnreviewed"/> is whether the
    /// unit's wave is running under the <c>proceed-unreviewed</c> opt-in — when true, a clamped
    /// <c>high</c>/<c>critical</c> hard call is NON-ANSWERABLE (§7.3 Blocker 1). Returns an
    /// <see cref="AnswerConsumptionResult"/> describing the outcome; NEVER blocks.
    /// </summary>
    public AnswerConsumptionResult Consume(
        int seq, string gate, string currentDefinitionHash, bool proceedUnreviewed = false)
    {
        // Inputs the implementation task will consume (referenced here so the stub compiles clean under
        // TreatWarningsAsErrors); the stub itself validates/injects nothing — it is TDD red.
        _ = (_escalationsDir, seq, gate, currentDefinitionHash, proceedUnreviewed);
        throw new NotImplementedException(
            "AnswerFileConsumer.Consume is a TDD-red stub: the binding validation (identity echo + dual-hash "
            + "staleness + answerable-gate + monotonic-seq uniqueness), the CAS-guarded once-only status flip "
            + "to 'consumed', the delimited untrusted-data injection, and the 'answer-injected' decision land "
            + "in the implementation task (doc 12 §7.4/§7.5/§7.6).");
    }
}

/// <summary>
/// The outcome of an <see cref="AnswerFileConsumer.Consume"/> attempt (doc 12 §7.6): the answer was
/// <see cref="Injected"/> (valid ⇒ consumed once + injected/applied), <see cref="Rejected"/> (present but
/// failed a binding/answerability/staleness check ⇒ re-escalate with the reason recorded), or
/// <see cref="NoAnswer"/> (none present ⇒ re-escalate unchanged — the graceful-degrade default).
/// </summary>
public enum AnswerOutcome
{
    /// <summary>No pending answer file was present — the gate re-escalates exactly as a plain forensic halt (§7.6.4).</summary>
    NoAnswer,

    /// <summary>A valid, bound, fresh, unconsumed answer was consumed ONCE — injected (<c>needs-human</c>) or applied (<c>wave-checkpoint</c>); the escalation <c>status</c> flipped to <c>consumed</c>.</summary>
    Injected,

    /// <summary>An answer was present but failed validation (wrong binding / stale hash / non-answerable gate / already consumed / malformed) — it is rejected and the gate re-escalates.</summary>
    Rejected
}

/// <summary>
/// The result of a consumption attempt (doc 12 §7.6). On <see cref="AnswerOutcome.Injected"/> it carries the
/// recorded <see cref="Decision"/> (<see cref="DecisionTokens.AnswerInjected"/> with provenance) plus the
/// gate-specific effect (<see cref="InjectedPromptSection"/> for <c>needs-human</c>, <see cref="WaveDecision"/>
/// for <c>wave-checkpoint</c>); on <see cref="AnswerOutcome.Rejected"/>/<see cref="AnswerOutcome.NoAnswer"/> it
/// carries the <see cref="RejectionReason"/> and sets <see cref="ReEscalated"/>.
/// </summary>
public sealed record AnswerConsumptionResult
{
    /// <summary>How the attempt resolved.</summary>
    public required AnswerOutcome Outcome { get; init; }

    /// <summary>The <see cref="DecisionTokens.AnswerInjected"/> decision recorded on a successful consumption (carrying <see cref="DecisionEntry.AnswerRef"/>/<see cref="DecisionEntry.AnsweredBy"/> provenance and the bound escalation id); null otherwise.</summary>
    public DecisionEntry? Decision { get; init; }

    /// <summary>(<c>needs-human</c> injection) The next attempt's composed-prompt section wrapping the answer <c>text</c> as clearly-delimited UNTRUSTED human-answer DATA (§7.4 Finding 4); null otherwise.</summary>
    public string? InjectedPromptSection { get; init; }

    /// <summary>(<c>wave-checkpoint</c> injection) The applied checkpoint decision — <c>proceed</c> or <c>hold</c>; null otherwise.</summary>
    public string? WaveDecision { get; init; }

    /// <summary>On <see cref="AnswerOutcome.Rejected"/>: the recorded reason the answer bounced (surfaced to firstmate so it can see WHY, §7.6.4); null on a clean injection.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>True when the gate re-escalates (no answer, or a rejected answer) rather than proceeding.</summary>
    public bool ReEscalated { get; init; }
}
