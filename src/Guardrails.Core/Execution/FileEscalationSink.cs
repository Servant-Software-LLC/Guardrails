using Guardrails.Core.Journal;

namespace Guardrails.Core.Execution;

/// <summary>
/// The default, file-based <see cref="IEscalationSink"/> (doc 12 §7.2). On <see cref="Escalate"/> it:
/// <list type="number">
///   <item>writes a structured record to <c>logs/&lt;runId&gt;/escalations/&lt;seq&gt;-&lt;gate&gt;.json</c> —
///   the serialized <see cref="EscalationRequest"/> plus the assigned <see cref="EscalationId"/> and a
///   <c>status: "open"</c> (carrying the <see cref="EscalationRequest.DefinitionHash"/>);</item>
///   <item>appends a <c>decisions[]</c> <see cref="DecisionTokens.Escalated"/> entry (the §6.2 fields —
///   gate, criticality, threshold) via <see cref="RunJournal.RecordDecision"/> — the durable audit;</item>
///   <item>emits <see cref="IRunObserver.DecisionRecorded"/> so a live UI / stdout shows the escalation.</item>
/// </list>
/// The <c>seq</c> is allocated from a persisted, run-level counter (journaled — NOT derived from the
/// <c>escalations/</c> directory listing), so it stays strictly monotonic across resumes and is never
/// reused. <see cref="Escalate"/> NEVER blocks: it records and returns the id; the reply arrives out of
/// band (§7.1).
///
/// TDD-RED STUB (issue #361 Phase 3, task <c>10-author-tests-escalation-sink</c>): <see cref="Escalate"/>
/// throws so the escalation-sink tests COMPILE and FAIL. The real record write, the run-level <c>seq</c>
/// allocation, and the <c>decisions[]</c> append are wired by the sibling implementation task (which also
/// adds the journaled counter to <see cref="RunJournal"/>); this stub stays self-contained and touches no
/// shipped type.
/// </summary>
public sealed class FileEscalationSink : IEscalationSink
{
    private readonly string _logsRoot;
    private readonly RunJournal _journal;
    private readonly IRunObserver _observer;
    private readonly string _escalationThreshold;

    /// <summary>
    /// Construct the sink over the run's <paramref name="logsRoot"/> (the <c>logs/</c> dir; records land
    /// under <c>&lt;logsRoot&gt;/&lt;runId&gt;/escalations/</c>, where <c>runId</c> is
    /// <see cref="RunJournal"/>'s), the <paramref name="journal"/> (the single writer of <c>decisions[]</c>
    /// and the run-level <c>seq</c> counter), the <paramref name="observer"/> that surfaces the decision
    /// live, and the <paramref name="escalationThreshold"/> in force at the run — recorded on the
    /// <c>decisions[]</c> entry's <c>threshold</c> field (doc 12 §6.2).
    /// </summary>
    public FileEscalationSink(
        string logsRoot, RunJournal journal, IRunObserver observer, string escalationThreshold)
    {
        _logsRoot = logsRoot;
        _journal = journal;
        _observer = observer;
        _escalationThreshold = escalationThreshold;
    }

    /// <inheritdoc />
    public EscalationId Escalate(EscalationRequest request)
    {
        // Dependencies the implementation task will consume (referenced here so the stub compiles clean
        // under TreatWarningsAsErrors); the stub itself records nothing — it is TDD red.
        _ = (_logsRoot, _journal, _observer, _escalationThreshold, request);
        throw new NotImplementedException(
            "FileEscalationSink.Escalate is a TDD-red stub: the escalations/<seq>-<gate>.json write, the "
            + "run-level seq allocation, the decisions[] 'escalated' append, and the DecisionRecorded emit "
            + "land in the implementation task.");
    }
}
