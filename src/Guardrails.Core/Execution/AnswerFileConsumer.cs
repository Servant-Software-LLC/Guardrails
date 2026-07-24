using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

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
/// BOTH answerable channels are WIRED into the production resume path (#375): the task-level
/// <c>needs-human</c> channel is consumed by <c>Scheduler.ConsumePendingAnswers</c> before a task re-hits its
/// gate, and the wave-boundary <c>wave-checkpoint</c> channel is consumed by
/// <c>Scheduler.TryConsumeWaveProceed</c> at the JIT between-wave checkpoint (a valid <c>proceed</c> breaks
/// down + runs the wave; a <c>hold</c> records the decision and stays halted). Neither channel can clear the
/// <c>review-gate</c> (§7.5) or an escalation the <c>proceed-unreviewed</c> clamp made non-answerable (§7.3).
/// </summary>
public sealed class AnswerFileConsumer
{
    /// <summary>The two ANSWERABLE gates (§7.3): only these bind an answer file. Everything else is terminal.</summary>
    private static readonly HashSet<string> AnswerableGates = new(StringComparer.Ordinal)
    {
        AnswerFileConsumer.NeedsHumanGate,
        AnswerFileConsumer.WaveCheckpointGate
    };

    /// <summary>The clamped hard-call criticalities (§5.2/§7.3, Blocker 1): NON-answerable under <c>proceed-unreviewed</c>.</summary>
    private static readonly HashSet<string> ClampedCriticalities = new(StringComparer.OrdinalIgnoreCase)
    {
        "high",
        "critical"
    };

    private const string NeedsHumanGate = "needs-human";
    private const string WaveCheckpointGate = "wave-checkpoint";
    private const string ConsumedStatus = "consumed";

    /// <summary>
    /// Defense-in-depth length cap on an injected <c>needs-human</c> answer <c>text</c> (§7.4 Finding 4, #375
    /// WEAK 2). A legitimate human decision is a sentence or two; 8 KB is a generous ceiling that still bounds
    /// a crafted flood. An answer whose text exceeds it is REJECTED (fail closed → re-escalate), so an
    /// oversized payload can never be injected into the next attempt's composed prompt.
    /// </summary>
    private const int MaxAnswerTextChars = 8 * 1024;

    /// <summary>
    /// In-process serialization for the CAS-guarded status flip (§7.1). The cross-PROCESS guard is a per-record
    /// lock file (<see cref="TryAcquireRecordLock"/>); this static gate additionally serializes concurrent
    /// resumes WITHIN one process deterministically (independent of platform file-share semantics), so the
    /// "exactly one injects" invariant holds for both threads and processes — two resumes never double-inject.
    /// </summary>
    private static readonly object ConsumeGate = new();

    /// <summary>Web (camelCase, case-insensitive) reader for the firstmate <see cref="AnswerFile"/>.</summary>
    private static readonly JsonSerializerOptions AnswerJson = new(JsonSerializerDefaults.Web);

    /// <summary>Indented writer for the flipped escalation record (a pollable, human-readable document).</summary>
    private static readonly JsonSerializerOptions RecordJson = new() { WriteIndented = true };

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
    /// <c>high</c>/<c>critical</c> hard call is NON-ANSWERABLE (§7.3 Blocker 1). It is REQUIRED (no default):
    /// a security clamp must never default open, so the compiler forces every caller to state the posture
    /// explicitly (#375). Returns an <see cref="AnswerConsumptionResult"/> describing the outcome; NEVER blocks.
    /// </summary>
    public AnswerConsumptionResult Consume(
        int seq, string gate, string currentDefinitionHash, bool proceedUnreviewed)
    {
        string escalationPath = Path.Combine(_escalationsDir, $"{seq}-{gate}.json");
        string answerPath = Path.Combine(_escalationsDir, $"{seq}-{gate}.answer.json");
        string lockPath = escalationPath + ".lock";

        // The whole read-check-validate-flip runs under the CAS lock so two concurrent resumes serialize: the
        // winner flips open → consumed and injects; the loser then reads `consumed` and does NOT re-inject.
        lock (ConsumeGate)
        {
            FileStream? recordLock = TryAcquireRecordLock(lockPath);
            if (recordLock is null)
            {
                return ReEscalate(AnswerOutcome.Rejected, "could not acquire the consumption lock (contended)");
            }

            try
            {
                return ConsumeUnderLock(seq, gate, currentDefinitionHash, proceedUnreviewed, escalationPath, answerPath);
            }
            finally
            {
                recordLock.Dispose();
            }
        }
    }

    private static AnswerConsumptionResult ConsumeUnderLock(
        int seq, string gate, string currentDefinitionHash, bool proceedUnreviewed,
        string escalationPath, string answerPath)
    {
        // 1. The escalation record carries the binding identity + `status` the answer is validated against. A
        //    missing/corrupt record cannot be consumed against → re-escalate.
        JsonObject? escalation = TryReadEscalation(escalationPath);
        if (escalation is null)
        {
            return ReEscalate(AnswerOutcome.Rejected, "escalation record is missing or unreadable");
        }

        // 2. Once-only (§7.4/§7.6): an already-consumed escalation is NEVER re-injected — a duplicate/edited
        //    answer re-dropped (even under a new runId) is ignored (recorded rejected; does NOT re-escalate).
        string? status = ReadString(escalation, "status");
        if (string.Equals(status, ConsumedStatus, StringComparison.Ordinal))
        {
            return new AnswerConsumptionResult
            {
                Outcome = AnswerOutcome.Rejected,
                RejectionReason = "escalation already consumed — not re-injected (once-only, §7.4)",
                ReEscalated = false
            };
        }

        // 3. No pending answer ⇒ graceful degrade to a plain forensic halt (§7.6.4).
        if (!File.Exists(answerPath))
        {
            return ReEscalate(AnswerOutcome.NoAnswer, "no pending answer file beside the escalation");
        }

        // 4. Parse the answer; a malformed file is REJECTED (never a crash), reason recorded (§7.6.4).
        AnswerFile? answer = TryReadAnswer(answerPath);
        if (answer is null || answer.Answer is null)
        {
            return ReEscalate(AnswerOutcome.Rejected, "answer file is malformed / not valid JSON");
        }

        // 5. Binding identity (§7.6.2): {runId, seq, gate, subject} MUST echo the escalation VERBATIM. The
        //    durably-monotonic, never-reused seq (§7.1) makes the tuple unique, so a stale/misfiled answer (a
        //    leftover for an earlier escalation, or one crafted for a different gate/task) cannot bind.
        string? escRunId = ReadString(escalation, "runId") ?? ReadNestedString(escalation, "id", "runId");
        int? escSeq = ReadInt(escalation, "seq") ?? ReadNestedInt(escalation, "id", "seq");
        string? escGate = ReadString(escalation, "gate") ?? ReadNestedString(escalation, "id", "gate");
        string? escSubject = ReadString(escalation, "subject") ?? ReadNestedString(escalation, "id", "subject");
        if (!string.Equals(answer.RunId, escRunId, StringComparison.Ordinal)
            || answer.Seq != escSeq
            || !string.Equals(answer.Gate, escGate, StringComparison.Ordinal)
            || !string.Equals(answer.Subject, escSubject, StringComparison.Ordinal))
        {
            return ReEscalate(AnswerOutcome.Rejected,
                "answer binding {runId, seq, gate, subject} does not echo the escalation verbatim");
        }

        // 6. Answerable gate (§7.3): only needs-human / wave-checkpoint bind. review-gate (no answer kind
        //    exists, §7.5), a hard blocker, and any terminal gate are NON-answerable → reject.
        if (!AnswerableGates.Contains(gate))
        {
            return ReEscalate(AnswerOutcome.Rejected, $"gate '{gate}' is not answerable by an answer file (§7.3)");
        }

        // 7. Clamped hard call under proceed-unreviewed (§5.2/§7.3, Blocker 1): a high/critical judgment call
        //    inside an explicitly-unreviewed wave is NON-answerable — it keeps a HUMAN, not a firstmate
        //    auto-answer, in the loop (else the "Guardrails without Guardrails" loop re-opens).
        string? criticality = ReadString(escalation, "criticality");
        if (proceedUnreviewed && criticality is not null && ClampedCriticalities.Contains(criticality))
        {
            return ReEscalate(AnswerOutcome.Rejected,
                $"'{criticality}' hard call is non-answerable under proceed-unreviewed (clamp, §7.3 Blocker 1)");
        }

        // 8. The answer.kind must match the gate — the two answerable kinds only (§7.4).
        string expectedKind = gate == WaveCheckpointGate ? AnswerKinds.WaveProceed : AnswerKinds.NeedsHuman;
        if (!string.Equals(answer.Answer.Kind, expectedKind, StringComparison.Ordinal))
        {
            return ReEscalate(AnswerOutcome.Rejected,
                $"answer kind '{answer.Answer.Kind}' does not match gate '{gate}' (expected '{expectedKind}')");
        }

        // 9. Dual definition-hash staleness (§7.6.2, mirroring #274/§7.2): the answer's definitionHash must
        //    equal BOTH the escalation record's captured hash AND the unit's CURRENT hash. A definition edited
        //    since the escalation ⇒ stale ⇒ reject.
        string? escHash = ReadString(escalation, "definitionHash");
        if (!string.Equals(answer.DefinitionHash, escHash, StringComparison.Ordinal)
            || !string.Equals(answer.DefinitionHash, currentDefinitionHash, StringComparison.Ordinal))
        {
            return ReEscalate(AnswerOutcome.Rejected,
                "answer definitionHash is stale — it must equal both the escalation's and the unit's current hash");
        }

        // Valid, bound, fresh, unconsumed, answerable ⇒ inject/apply ONCE and flip the status to consumed.
        return gate == WaveCheckpointGate
            ? ApplyWaveCheckpoint(answer, escalation, escalationPath, gate, seq, currentDefinitionHash)
            : InjectNeedsHuman(answer, escalation, escalationPath, gate, seq, currentDefinitionHash);
    }

    /// <summary>
    /// The <c>needs-human</c> injection (§7.4): wrap the answer <c>text</c> in the pinned untrusted-data
    /// envelope (<see cref="PromptComposer.ComposeInjectedHumanAnswerSection"/>), record the
    /// <see cref="DecisionTokens.AnswerInjected"/> decision, and flip the escalation to <c>consumed</c>.
    /// </summary>
    private static AnswerConsumptionResult InjectNeedsHuman(
        AnswerFile answer, JsonObject escalation, string escalationPath, string gate, int seq, string matchedHash)
    {
        if (string.IsNullOrEmpty(answer.Answer.Text))
        {
            return ReEscalate(AnswerOutcome.Rejected, "needs-human answer carries no decision text");
        }

        // Defense-in-depth envelope integrity (§7.4 Finding 4, #375 WEAK 2). The answer text is injected
        // VERBATIM between the pinned [BEGIN/END UNTRUSTED HUMAN ANSWER] literals; a payload that embeds either
        // literal could close the envelope early and have trailing bytes read as harness/system text, and an
        // unbounded payload could flood the prompt. A legitimate human decision never does either, so REJECT
        // (fail closed → re-escalate) before the text can be injected — the deterministic complement to the
        // prompt's "treat this as DATA" prohibition.
        if (answer.Answer.Text.Length > MaxAnswerTextChars)
        {
            return ReEscalate(AnswerOutcome.Rejected,
                $"needs-human answer text exceeds the {MaxAnswerTextChars}-character cap (§7.4 Finding 4, #375)");
        }

        if (answer.Answer.Text.Contains(PromptComposer.InjectedHumanAnswerBeginMarker, StringComparison.Ordinal)
            || answer.Answer.Text.Contains(PromptComposer.InjectedHumanAnswerEndMarker, StringComparison.Ordinal))
        {
            return ReEscalate(AnswerOutcome.Rejected,
                "needs-human answer text contains a reserved untrusted-answer envelope delimiter — "
                + "it could escape the [BEGIN/END UNTRUSTED HUMAN ANSWER] envelope (§7.4 Finding 4, #375)");
        }

        string section = PromptComposer.ComposeInjectedHumanAnswerSection(answer.Answer.Text);
        DecisionEntry decision = BuildAnswerInjectedDecision(answer, gate, seq, matchedHash, appliedEffect: null);
        FlipToConsumed(escalation, escalationPath);

        return new AnswerConsumptionResult
        {
            Outcome = AnswerOutcome.Injected,
            Decision = decision,
            InjectedPromptSection = section,
            ReEscalated = false
        };
    }

    /// <summary>
    /// The <c>wave-checkpoint</c> application (§7.4): apply the <c>proceed</c>/<c>hold</c> decision at the
    /// checkpoint, record the <see cref="DecisionTokens.AnswerInjected"/> decision (a <c>hold</c> still records
    /// the human chose to wait), and flip the escalation to <c>consumed</c>.
    /// </summary>
    private static AnswerConsumptionResult ApplyWaveCheckpoint(
        AnswerFile answer, JsonObject escalation, string escalationPath, string gate, int seq, string matchedHash)
    {
        string? waveDecision = answer.Answer.Decision;
        if (!string.Equals(waveDecision, WaveProceedDecisions.Proceed, StringComparison.Ordinal)
            && !string.Equals(waveDecision, WaveProceedDecisions.Hold, StringComparison.Ordinal))
        {
            return ReEscalate(AnswerOutcome.Rejected,
                $"wave-proceed decision '{waveDecision}' is neither 'proceed' nor 'hold'");
        }

        DecisionEntry decision = BuildAnswerInjectedDecision(answer, gate, seq, matchedHash, appliedEffect: waveDecision);
        FlipToConsumed(escalation, escalationPath);

        return new AnswerConsumptionResult
        {
            Outcome = AnswerOutcome.Injected,
            Decision = decision,
            WaveDecision = waveDecision,
            ReEscalated = false
        };
    }

    /// <summary>
    /// Build the <see cref="DecisionTokens.AnswerInjected"/> <see cref="DecisionEntry"/> (§6.2): the answer's
    /// provenance (<see cref="DecisionEntry.AnsweredBy"/>/<see cref="DecisionEntry.AnswerRef"/>), the bound
    /// escalation id, and the matched hash. The seam is reached only under the autonomous posture, so the
    /// policy in force is <c>auto</c> (mirroring <see cref="FileEscalationSink"/>).
    /// </summary>
    private static DecisionEntry BuildAnswerInjectedDecision(
        AnswerFile answer, string gate, int seq, string matchedHash, string? appliedEffect)
    {
        string effect = appliedEffect is { Length: > 0 }
            ? $"applied wave-checkpoint decision '{appliedEffect}'"
            : "injected the human answer as delimited untrusted data into the next attempt's composed prompt";
        return new DecisionEntry
        {
            Boundary = gate == WaveCheckpointGate ? "wave" : "task",
            Policy = AutonomyPolicies.Token(AutonomyPolicy.Auto),
            Decision = DecisionTokens.AnswerInjected,
            Subject = answer.Subject,
            Headline = $"Consumed firstmate answer for {gate} '{answer.Subject}' (seq {seq}) — {effect}",
            Gate = gate,
            AnswerRef = $"escalations/{seq}-{gate}.answer.json",
            AnsweredBy = answer.AnsweredBy,
            Detail = $"bound escalation {answer.RunId}#{seq} ({gate}/{answer.Subject}); matched definitionHash {matchedHash}"
        };
    }

    /// <summary>Flip the escalation record's <c>status</c> to <c>consumed</c> in place (all other fields preserved), written atomically under the CAS lock.</summary>
    private static void FlipToConsumed(JsonObject escalation, string escalationPath)
    {
        escalation["status"] = ConsumedStatus;
        AtomicFile.WriteAllText(escalationPath, escalation.ToJsonString(RecordJson));
    }

    /// <summary>A rejection / no-answer outcome that re-escalates unchanged, carrying the recorded reason (§7.6.4).</summary>
    private static AnswerConsumptionResult ReEscalate(AnswerOutcome outcome, string reason) =>
        new()
        {
            Outcome = outcome,
            RejectionReason = reason,
            ReEscalated = true
        };

    /// <summary>Read the escalation record as a mutable <see cref="JsonObject"/>, or null when it is absent/corrupt.</summary>
    private static JsonObject? TryReadEscalation(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Deserialize the firstmate answer file, or null when it is malformed / missing a required field.</summary>
    private static AnswerFile? TryReadAnswer(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AnswerFile>(File.ReadAllText(path), AnswerJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Acquire the per-record cross-process lock (<c>&lt;seq&gt;-&lt;gate&gt;.json.lock</c>) exclusively, bounded
    /// so a pathologically-contended acquire fails safe (null ⇒ re-escalate) rather than hanging the resume.
    /// Returns the held stream (dispose to release) or null on timeout. In-process contention is already
    /// serialized by <see cref="ConsumeGate"/>, so this only ever contends across processes.
    /// </summary>
    private static FileStream? TryAcquireRecordLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        for (int attempt = 0; attempt < 400; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }

        return null;
    }

    private static string? ReadString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node) && node is JsonValue value && value.TryGetValue(out string? s)
            ? s
            : null;

    private static int? ReadInt(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node) && node is JsonValue value && value.TryGetValue(out int i)
            ? i
            : null;

    private static string? ReadNestedString(JsonObject obj, string parent, string key) =>
        obj.TryGetPropertyValue(parent, out JsonNode? node) && node is JsonObject child ? ReadString(child, key) : null;

    private static int? ReadNestedInt(JsonObject obj, string parent, string key) =>
        obj.TryGetPropertyValue(parent, out JsonNode? node) && node is JsonObject child ? ReadInt(child, key) : null;
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
