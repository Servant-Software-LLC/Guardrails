namespace Guardrails.Core.Execution;

/// <summary>
/// The firstmate escalation seam (doc 12 §7.1/§7.2, issue #361 Phase 3). At every judgment gate an
/// unattended run cannot best-guess below the dial, the harness ESCALATES: it records the question, the
/// full reconstruction context, and a binding identity to a well-known path — and then RETURNS. It never
/// blocks waiting for a reply (§7.1/§7.3): firstmate (or any orchestrator) polls those files out of band
/// and writes an answer file a later <c>resume</c> consumes. This mirrors the shipped
/// <see cref="IRunObserver"/> / <c>IOverwatchInteraction</c> seams — plain files, no daemon/socket/queue
/// (invariant 6). The production implementation is <see cref="FileEscalationSink"/>.
/// </summary>
public interface IEscalationSink
{
    /// <summary>
    /// Record the escalation and signal it — write <c>logs/&lt;runId&gt;/escalations/&lt;seq&gt;-&lt;gate&gt;.json</c>,
    /// append a <c>decisions[]</c> <see cref="DecisionTokens.Escalated"/> entry (via
    /// <see cref="Journal.RunJournal.RecordDecision"/>), and emit <see cref="IRunObserver.DecisionRecorded"/>.
    /// Returns the assigned <see cref="EscalationId"/> IMMEDIATELY — fire-and-record, never a blocking wait
    /// for a reply (§7.1). The answer arrives later, out of band, and is consumed by a resume (§7.6).
    /// </summary>
    EscalationId Escalate(EscalationRequest request);
}

/// <summary>
/// The payload of a single escalation (doc 12 §7.1). Carries the human-answerable <see cref="Question"/>,
/// the full reconstruction <see cref="Context"/>, and the <see cref="DefinitionHash"/> captured at
/// escalation time — the anti-stale binding (the same drift discipline as #274/§7.2) an answer file must
/// match to bind. A stale answer (the task/wave definition changed since the escalation) is rejected and
/// re-escalated.
/// </summary>
public sealed record EscalationRequest
{
    /// <summary>The gate: <c>needs-human</c> · <c>wave-checkpoint</c> · <c>review-gate</c> · <c>blocker</c> (all escalate; only <c>needs-human</c>/<c>wave-checkpoint</c> are answerable, §7.2).</summary>
    public required string Gate { get; init; }

    /// <summary>The unit the escalation concerns — a task id (needs-human) or a wave dir (wave-checkpoint/review-gate).</summary>
    public required string Subject { get; init; }

    /// <summary>The human-answerable question.</summary>
    public required string Question { get; init; }

    /// <summary>
    /// The bounded, enumerated options a structured <c>needsHuman</c> carried (issue #387), in order. Empty for
    /// a free-text <c>needsHuman</c> and for a non-answerable gate. When present on an ANSWERABLE, un-clamped
    /// escalation, a pick surface (interactive <c>SelectionPrompt</c> / web button) presents them and writes the
    /// chosen one as the answer <c>text</c> — the choice is injected through the SAME <see cref="AnswerFileConsumer"/>
    /// envelope as any answer (delimited untrusted data), never a trusted directive.
    /// </summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>
    /// The agent's optional classification of a <c>needs-human</c> escalation (issue #485):
    /// <see cref="NeedsHumanKinds.BlockedWork"/> or <see cref="NeedsHumanKinds.DefectiveGuardrail"/>. Null
    /// for an unclassified escalation and for every non-<c>needs-human</c> gate. Recorded verbatim on the
    /// escalation record so a downstream reader (a <c>jq</c> over <c>logs/&lt;runId&gt;/escalations/*.json</c>,
    /// the #479 probe corpus) can select the defective-guardrail claims without parsing prose. It is a CLAIM,
    /// not a verdict — the harness never derives or defaults it.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>Full context to reconstruct the gate: logs pointers, failure detail, and the best-guess considered.</summary>
    public required string Context { get; init; }

    /// <summary>The assessed criticality (<c>low</c>·<c>moderate</c>·<c>high</c>·<c>critical</c>); null for a hard blocker.</summary>
    public string? Criticality { get; init; }

    /// <summary>The definition hash captured at escalation time (<c>TaskDefinitionHash</c> for a needs-human, <c>WaveDefinitionHash</c> for a wave) — the anti-stale binding an answer must echo.</summary>
    public required string DefinitionHash { get; init; }

    /// <summary>UTC time the escalation was raised (ISO-8601).</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>
/// The escalation's binding identity (doc 12 §7.1): what an answer file must echo verbatim to bind (§7.4).
/// The <see cref="Seq"/> is durably monotonic per run and never reused across resumes (allocated from a
/// persisted run-level counter, not a directory listing), so the tuple is unique for the life of the run.
/// </summary>
public sealed record EscalationId(string RunId, int Seq, string Gate, string Subject);
