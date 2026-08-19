using System.Text.Json;
using System.Text.Json.Serialization;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;

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
/// This is a THIN layer over shipped machinery (§7.2), NOT a new transport: it writes plain files
/// (invariant 6), delegates the durable audit to <see cref="RunJournal.RecordDecision"/>, and surfaces the
/// live event through <see cref="IRunObserver"/>. The harness never knows firstmate exists — it writes files
/// and (elsewhere) sets an exit code; polling/answering happens out of band.
/// </summary>
public sealed class FileEscalationSink : IEscalationSink
{
    /// <summary>The initial record status (§7.6 <c>open → answered → consumed</c>); a later resume flips it.</summary>
    private const string OpenStatus = "open";

    /// <summary>
    /// Pretty-printed, camelCase, omit-null serializer for the human-/firstmate-inspectable escalation
    /// record (contrast the compact single-line <c>autonomy.jsonl</c> stream) — a record is a pollable
    /// document a human may read, so it is indented.
    /// </summary>
    private static readonly JsonSerializerOptions RecordJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
        // 1. Allocate the durably-monotonic, never-reused run-level seq (doc 12 §7.1, Finding 5) — journaled,
        //    NOT derived from the escalations/ listing — and bind the escalation's identity.
        string runId = _journal.Document.RunId;
        int seq = _journal.NextEscalationSeq();
        var id = new EscalationId(runId, seq, request.Gate, request.Subject);

        // 2. Write the structured record atomically to the well-known, pollable path
        //    logs/<runId>/escalations/<seq>-<gate>.json: the serialized request + the assigned id + status
        //    "open", carrying the anti-stale DefinitionHash. This is the machine-readable question (§7.2).
        string recordPath = Path.Combine(_logsRoot, runId, "escalations", $"{seq}-{request.Gate}.json");
        var record = new EscalationRecord
        {
            Gate = request.Gate,
            Subject = request.Subject,
            Question = request.Question,
            Context = request.Context,
            Criticality = request.Criticality,
            DefinitionHash = request.DefinitionHash,
            At = request.At,
            Id = id,
            Status = OpenStatus,
            Options = request.Options,
            Kind = request.Kind
        };
        AtomicFile.WriteAllText(recordPath, JsonSerializer.Serialize(record, RecordJson));

        // 3 + 4. Build the 'escalated' decisions[] entry (the §6.2 fields) ONCE, append it to the durable
        //    audit via the shipped single-writer RunJournal.RecordDecision, and emit the SAME entry live so a
        //    UI / stdout shows the escalation as it happens (§7.2).
        var decision = new DecisionEntry
        {
            Boundary = BoundaryFor(request.Gate),
            // The sink is the AUTONOMOUS escalation seam: it is reached only when the run is classifying gates
            // itself rather than prompting/halting — so 'auto' is the policy in force at this boundary.
            Policy = AutonomyPolicies.Token(AutonomyPolicy.Auto),
            Decision = DecisionTokens.Escalated,
            Subject = request.Subject,
            Headline = Headline(request),
            At = request.At,
            Gate = request.Gate,
            Criticality = request.Criticality,
            Threshold = _escalationThreshold
        };
        _journal.RecordDecision(decision);
        _observer.DecisionRecorded(decision);

        // 5. Never block: return the assigned id immediately. The answer arrives out of band and is consumed
        //    by a later resume (§7.1) — no wait here, and no answer file is synthesized.
        return id;
    }

    /// <summary>Map the gate to the §6.2 <c>boundary</c> discriminator: a wave gate → <c>wave</c>; everything else → <c>task</c>.</summary>
    private static string BoundaryFor(string gate) => gate switch
    {
        "wave-checkpoint" => "wave",
        "review-gate" => "wave",
        _ => "task"
    };

    /// <summary>A one-line human headline for the live UI / <c>decisions[]</c> audit.</summary>
    private static string Headline(EscalationRequest request)
    {
        string criticality = request.Criticality is { Length: > 0 } c ? $" (criticality {c})" : "";
        return $"Escalated {request.Gate} for '{request.Subject}'{criticality} — awaiting a human answer";
    }

    /// <summary>
    /// The on-disk escalation record (doc 12 §7.1): the serialized <see cref="EscalationRequest"/> fields, the
    /// assigned <see cref="EscalationId"/> (<see cref="Id"/>, so the <c>{ runId, seq, gate, subject }</c>
    /// binding lives in the body, not just the path), and the <c>open</c> <see cref="Status"/> a later resume
    /// flips through <c>answered → consumed</c> (§7.6). Null <see cref="Criticality"/> (a hard blocker) is
    /// omitted via <see cref="RecordJson"/>'s omit-null policy.
    /// </summary>
    private sealed record EscalationRecord
    {
        public required string Gate { get; init; }
        public required string Subject { get; init; }
        public required string Question { get; init; }
        public required string Context { get; init; }
        public string? Criticality { get; init; }
        public required string DefinitionHash { get; init; }
        public required DateTimeOffset At { get; init; }
        public required EscalationId Id { get; init; }
        public required string Status { get; init; }

        /// <summary>The structured-<c>needsHuman</c> options (issue #387), in order; empty (omitted via the omit-null policy is NOT applied to an empty list, so it serializes as <c>[]</c>) for a free-text or non-answerable escalation.</summary>
        public IReadOnlyList<string> Options { get; init; } = [];

        /// <summary>
        /// The agent's <c>needsHuman.kind</c> claim (issue #485) — <c>blocked-work</c> or
        /// <c>defective-guardrail</c>. Null (and, via <see cref="RecordJson"/>'s omit-null policy, ABSENT
        /// from the file) when unclassified, so a pre-#485-shaped escalation record is byte-identical.
        /// </summary>
        public string? Kind { get; init; }
    }
}
