using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// The shared "pick one option" surface machinery (issue #387) — a human resolves an enumerated
/// <c>needsHuman</c> decision by CHOOSING one of the agent's own options, instead of hand-authoring a reply
/// file (or editing the prompt and re-running). A pick is just ANOTHER writer into the EXISTING firstmate reply
/// channel (doc 12 §7.4, SSOT §7.2): it writes a well-formed <see cref="AnswerFile"/> beside the open escalation
/// record, which a resume then consumes through the UNCHANGED <see cref="AnswerFileConsumer"/> path (the chosen
/// option is injected as delimited UNTRUSTED data, never a trusted directive).
///
/// <para>BOTH pick surfaces call this ONE component so the non-answerable floor is enforced IDENTICALLY:
/// <list type="bullet">
///   <item><b>v1</b> — the interactive <c>Spectre SelectionPrompt</c> at an attended run's end.</item>
///   <item><b>v2</b> — the web <c>POST</c> from the log viewer's option buttons.</item>
/// </list>
/// Neither can offer or write an answer for a NON-answerable gate: <see cref="ReadOpen"/> marks each
/// escalation's <see cref="PickableEscalation.Answerable"/> (a <c>review-gate</c>, or a clamped
/// <c>high</c>/<c>critical</c> hard call under <c>proceed-unreviewed</c>, is NON-answerable — no buttons, no
/// pick, halt reason shown), and <see cref="WriteChoice"/> is the HARD floor that REFUSES to write for one
/// regardless of surface — reusing the SAME <see cref="AnswerableGates"/> predicate the consumer enforces on
/// consumption. A pick can NEVER forge a review marker (§7.5, #366): the writer only ever produces a
/// <c>needs-human</c> answer <c>text</c>, there is no answer kind that resolves the review gate, and it never
/// touches <c>state/guardrails-review.json</c>.</para>
/// </summary>
public static class EscalationPick
{
    private const string ConsumedStatus = "consumed";

    /// <summary>Web (camelCase) reader/writer for the escalation record + the emitted <see cref="AnswerFile"/> — the exact shape <see cref="AnswerFileConsumer"/> reads back.</summary>
    private static readonly JsonSerializerOptions AnswerJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Enumerate the OPEN, options-carrying <c>needs-human</c> escalations in <paramref name="escalationsDir"/>
    /// (<c>logs/&lt;runId&gt;/escalations/</c>) — the universe a pick surface can present. Each carries its
    /// <see cref="PickableEscalation.Options"/> and whether it is <see cref="PickableEscalation.Answerable"/>
    /// (false ⇒ a clamped hard call under <c>proceed-unreviewed</c>, §7.3 Blocker 1 — the UI shows
    /// <see cref="PickableEscalation.NonAnswerableReason"/> and renders NO buttons). An already-<c>consumed</c>
    /// escalation, a free-text (no-options) <c>needsHuman</c>, and every non-<c>needs-human</c> gate are
    /// excluded. Best-effort: an unreadable/corrupt record is skipped, never a crash.
    /// </summary>
    public static IReadOnlyList<PickableEscalation> ReadOpen(string escalationsDir, bool proceedUnreviewed)
    {
        if (!Directory.Exists(escalationsDir))
        {
            return [];
        }

        var result = new List<PickableEscalation>();
        foreach (string recordPath in Directory.EnumerateFiles(escalationsDir, "*.json"))
        {
            if (recordPath.EndsWith(".answer.json", StringComparison.Ordinal))
            {
                continue; // the reply file, not an escalation record
            }

            if (TryReadRecord(recordPath) is not { } record)
            {
                continue;
            }

            // Only OPEN, options-carrying needs-human escalations are pickable (the #387 target). A free-text
            // needsHuman, a consumed one, and any other gate are not a pick surface.
            if (!string.Equals(record.Gate, AnswerableGates.NeedsHumanGate, StringComparison.Ordinal)
                || string.Equals(record.Status, ConsumedStatus, StringComparison.Ordinal)
                || record.Options.Count == 0)
            {
                continue;
            }

            bool clamped = AnswerableGates.IsClampedHardCall(record.Criticality, proceedUnreviewed);
            result.Add(new PickableEscalation
            {
                Seq = record.Seq,
                Gate = record.Gate,
                Subject = record.Subject,
                Question = record.Question,
                Options = record.Options,
                Criticality = record.Criticality,
                Answerable = !clamped,
                NonAnswerableReason = clamped
                    ? $"'{record.Criticality}' hard call is non-answerable under proceed-unreviewed (clamp, §7.3 Blocker 1) — resolve it with real human work, not a one-click pick."
                    : null
            });
        }

        result.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return result;
    }

    /// <summary>
    /// Write the operator's chosen option as a firstmate <see cref="AnswerFile"/> beside the open escalation
    /// <c>&lt;seq&gt;-&lt;gate&gt;.json</c> — the SINGLE writer both pick surfaces call, and the HARD floor that
    /// enforces the non-answerable refusal. Validates, in order: the record exists and is not already
    /// <c>consumed</c>; the gate is ANSWERABLE (<see cref="AnswerableGates.IsAnswerable"/>); it is not a clamped
    /// <c>high</c>/<c>critical</c> hard call under <paramref name="proceedUnreviewed"/>; and
    /// <paramref name="chosenOption"/> is one of the escalation's OWN options (a bounded pick — an off-menu
    /// choice is rejected). On success it writes <c>&lt;seq&gt;-&lt;gate&gt;.answer.json</c> echoing the record's
    /// binding (<c>runId</c>/<c>seq</c>/<c>gate</c>/<c>subject</c>/<c>definitionHash</c>) with a
    /// <c>needs-human</c> payload whose <c>text</c> is the chosen option — the exact shape
    /// <see cref="AnswerFileConsumer"/> consumes on the next resume. <paramref name="answeredBy"/> is the
    /// surface's provenance (recorded for audit, never an authorization). NEVER produces a review-gate answer
    /// and NEVER writes a review marker.
    /// </summary>
    public static PickWriteOutcome WriteChoice(
        string escalationsDir, int seq, string gate, string chosenOption, string answeredBy, bool proceedUnreviewed)
    {
        string recordPath = Path.Combine(escalationsDir, $"{seq}-{gate}.json");
        if (TryReadRecord(recordPath) is not { } record)
        {
            return new PickWriteOutcome(PickWriteResult.NotFound,
                $"No open escalation record for seq {seq} / gate '{gate}'.");
        }

        if (string.Equals(record.Status, ConsumedStatus, StringComparison.Ordinal))
        {
            return new PickWriteOutcome(PickWriteResult.AlreadyConsumed,
                $"Escalation seq {seq} ('{gate}') was already consumed — a further pick would be ignored (once-only, §7.4).");
        }

        // The non-answerable floor — the SAME predicate the consumer enforces (§7.3). A pick can never write
        // past it: a review-gate / blocker / terminal gate is not answerable, and a clamped high/critical hard
        // call under proceed-unreviewed is non-answerable by fiat (§5.2/§7.3, Blocker 1).
        if (!AnswerableGates.IsAnswerable(gate))
        {
            return new PickWriteOutcome(PickWriteResult.RefusedNonAnswerable,
                $"Gate '{gate}' is not answerable by a pick (§7.3) — no answer file was written.");
        }

        if (AnswerableGates.IsClampedHardCall(record.Criticality, proceedUnreviewed))
        {
            return new PickWriteOutcome(PickWriteResult.RefusedNonAnswerable,
                $"'{record.Criticality}' hard call is non-answerable under proceed-unreviewed (clamp, §7.3 Blocker 1) — no answer file was written.");
        }

        // Bounded pick: the chosen option MUST be one of the agent's OWN offered options (ordinal). An off-menu
        // submission (e.g. a crafted POST body) is rejected — the whole safety benefit of a pick is that it is
        // bounded to the agent's enumerated choices.
        if (!record.Options.Contains(chosenOption, StringComparer.Ordinal))
        {
            return new PickWriteOutcome(PickWriteResult.OptionNotOffered,
                $"Choice is not one of escalation seq {seq}'s offered options — no answer file was written.");
        }

        var answer = new AnswerFile
        {
            RunId = record.RunId,
            Seq = record.Seq,
            Gate = record.Gate,
            Subject = record.Subject,
            DefinitionHash = record.DefinitionHash,
            AnsweredBy = answeredBy,
            AnsweredAt = DateTimeOffset.UtcNow,
            Answer = new AnswerPayload { Kind = AnswerKinds.NeedsHuman, Text = chosenOption }
        };

        string answerPath = Path.Combine(escalationsDir, $"{seq}-{gate}.answer.json");
        AtomicFile.WriteAllText(answerPath, JsonSerializer.Serialize(answer, AnswerJson));
        return new PickWriteOutcome(PickWriteResult.Written,
            $"Recorded your choice for escalation seq {seq} ('{gate}') — resume to apply it.");
    }

    /// <summary>Read an escalation record's fields, tolerating either a top-level or nested-<c>id</c> shape; null on a missing/corrupt/incomplete record.</summary>
    private static EscalationRecordView? TryReadRecord(string recordPath)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(recordPath)) is not JsonObject record)
            {
                return null;
            }

            string? runId = Str(record, "runId") ?? NestedStr(record, "id", "runId");
            int? seq = Int(record, "seq") ?? NestedInt(record, "id", "seq");
            string? gate = Str(record, "gate") ?? NestedStr(record, "id", "gate");
            string? subject = Str(record, "subject") ?? NestedStr(record, "id", "subject");
            string? definitionHash = Str(record, "definitionHash");
            if (runId is null || seq is not { } s || gate is null || subject is null || definitionHash is null)
            {
                return null;
            }

            return new EscalationRecordView
            {
                RunId = runId,
                Seq = s,
                Gate = gate,
                Subject = subject,
                Question = Str(record, "question") ?? "",
                Context = Str(record, "context") ?? "",
                Criticality = Str(record, "criticality"),
                DefinitionHash = definitionHash,
                Status = Str(record, "status") ?? "open",
                Options = ReadOptions(record)
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadOptions(JsonObject record)
    {
        if (!record.TryGetPropertyValue("options", out JsonNode? node) || node is not JsonArray array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? s) && !string.IsNullOrEmpty(s))
            {
                list.Add(s);
            }
        }

        return list;
    }

    private static string? Str(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node) && node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static int? Int(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node) && node is JsonValue v && v.TryGetValue(out int i) ? i : null;

    private static string? NestedStr(JsonObject obj, string parent, string key) =>
        obj.TryGetPropertyValue(parent, out JsonNode? node) && node is JsonObject child ? Str(child, key) : null;

    private static int? NestedInt(JsonObject obj, string parent, string key) =>
        obj.TryGetPropertyValue(parent, out JsonNode? node) && node is JsonObject child ? Int(child, key) : null;

    /// <summary>The fields read from an escalation record for the pick surfaces.</summary>
    private sealed record EscalationRecordView
    {
        public required string RunId { get; init; }
        public required int Seq { get; init; }
        public required string Gate { get; init; }
        public required string Subject { get; init; }
        public required string Question { get; init; }
        public required string Context { get; init; }
        public string? Criticality { get; init; }
        public required string DefinitionHash { get; init; }
        public required string Status { get; init; }
        public required IReadOnlyList<string> Options { get; init; }
    }
}

/// <summary>
/// An OPEN, options-carrying <c>needs-human</c> escalation a pick surface can present (issue #387). When
/// <see cref="Answerable"/> is true the surface offers the <see cref="Options"/> (SelectionPrompt / buttons);
/// when false it shows <see cref="NonAnswerableReason"/> and renders NO pick (the non-answerable floor).
/// </summary>
public sealed record PickableEscalation
{
    public required int Seq { get; init; }
    public required string Gate { get; init; }
    public required string Subject { get; init; }
    public required string Question { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
    public string? Criticality { get; init; }

    /// <summary>True when a pick MAY be offered; false when the non-answerable floor applies (clamped hard call) — show <see cref="NonAnswerableReason"/> instead.</summary>
    public required bool Answerable { get; init; }

    /// <summary>Why no pick is offered, when <see cref="Answerable"/> is false; null otherwise.</summary>
    public string? NonAnswerableReason { get; init; }
}

/// <summary>The outcome of <see cref="EscalationPick.WriteChoice"/>.</summary>
public enum PickWriteResult
{
    /// <summary>A valid, bounded, answerable choice was written as a firstmate answer file — consumed on the next resume.</summary>
    Written,

    /// <summary>No open escalation record matched the seq/gate.</summary>
    NotFound,

    /// <summary>The escalation was already consumed — a further pick is ignored (once-only, §7.4).</summary>
    AlreadyConsumed,

    /// <summary>The gate is not answerable (review-gate / blocker / terminal), OR a clamped hard call under proceed-unreviewed — REFUSED, no file written (§7.3).</summary>
    RefusedNonAnswerable,

    /// <summary>The chosen text is not one of the escalation's OWN offered options — rejected (bounded pick).</summary>
    OptionNotOffered
}

/// <summary>The result + a human-readable message from <see cref="EscalationPick.WriteChoice"/>.</summary>
public sealed record PickWriteOutcome(PickWriteResult Result, string Message);
