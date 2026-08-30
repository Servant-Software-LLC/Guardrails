using System.Text.Json;

namespace Guardrails.Core.Execution;

/// <summary>
/// The parsed, best-effort result of the overwatcher's diagnose prompt (doc 11 §1/§5): a doomed-vs-
/// retryable classification, a human diagnosis, and the typed fix ops the judge proposed. The judge
/// PROPOSES; the harness classifies + decides. A malformed/absent/unstructured diagnose result parses to
/// <c>null</c> (advisory-never-gates: no action, the deterministic policy stands).
/// </summary>
public sealed record OverwatchProposal
{
    /// <summary>The judge's structural read: is another attempt worth granting, or is this doomed?</summary>
    public required OverwatchClassification Classification { get; init; }

    /// <summary>The precise human diagnosis (the "here is exactly why" the terminal <c>needs-human</c> lacked).</summary>
    public required string Diagnosis { get; init; }

    /// <summary>The typed fix ops the judge proposed (may be empty — a doomed verdict proposes none).</summary>
    public IReadOnlyList<OverwatchFixOp> Fixes { get; init; } = [];

    /// <summary>
    /// Parse a diagnose result string into an <see cref="OverwatchProposal"/>. Best-effort: returns null
    /// on absent/blank/non-JSON/non-object input or a missing <c>diagnosis</c> — the caller then takes NO
    /// action (advisory). Unknown fix kinds/fields are dropped, never guessed onto the allowlist.
    /// <para>Wire shape:
    /// <c>{ "classification": "doomed"|"retryable", "diagnosis": "...", "fixes": [ { "kind": "guidance",
    /// "guidance": "..." } | { "kind": "budget", "field": "maxTurns", "value": 40 } | { "kind":
    /// "file-edit", "path": "..." } | { "kind": "task-field", "field": "writeScope" } ] }</c>.</para>
    /// </summary>
    public static OverwatchProposal? TryParse(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(Unfence(resultText));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("diagnosis", out JsonElement diag) || diag.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string diagnosis = diag.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(diagnosis))
            {
                return null;
            }

            OverwatchClassification classification =
                root.TryGetProperty("classification", out JsonElement cls) && cls.ValueKind == JsonValueKind.String
                    && string.Equals(cls.GetString(), "doomed", StringComparison.OrdinalIgnoreCase)
                    ? OverwatchClassification.Doomed
                    : OverwatchClassification.Retryable;

            var fixes = new List<OverwatchFixOp>();
            if (root.TryGetProperty("fixes", out JsonElement fixesEl) && fixesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement fix in fixesEl.EnumerateArray())
                {
                    if (ParseFix(fix) is { } op)
                    {
                        fixes.Add(op);
                    }
                }
            }

            return new OverwatchProposal
            {
                Classification = classification,
                Diagnosis = diagnosis.Trim(),
                Fixes = fixes
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Strip a surrounding markdown code fence before parsing.
    /// <para>
    /// <b>Why this exists.</b> The diagnose judge answers in a fenced block — <c>```json</c>, newline, the
    /// object, newline, <c>```</c> — which is what a chat model does with a request for JSON unless
    /// something stops it. <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> sees the backticks,
    /// throws, and the whole verdict is dropped as "not a parseable verdict". On plan 28 that discarded TWO
    /// complete, correct diagnoses, one of which had already identified a real harness bug (#550) by reading
    /// <c>action-result.json</c> and noticing the attempt had actually succeeded. The harness had the answer
    /// and threw it away over three backticks.
    /// </para>
    /// <para>
    /// Deliberately narrow: it removes a fence that WRAPS the body and does nothing else. It does not hunt
    /// for an embedded object inside prose, because a judge that answered in prose has not produced a
    /// verdict and "advisory-never-gates" means the right outcome there is still null.
    /// </para>
    /// <para>
    /// <b>DISPOSITION — TEMPORARY, replace when #223 lands.</b> This is a LOCAL second implementation of
    /// lenient JSON extraction, and the repo is about to grow the real one: plan 28 task 05
    /// (<c>05-implement-shared-json-extractor</c>) builds a shared <c>PromptJsonExtractor</c>, which did not
    /// exist on master when #551 had to be fixed. When it lands, delete this method and route
    /// <see cref="TryParse"/> through it — one extractor, not two that drift apart while appearing to agree.
    /// The behaviour to preserve on that swap is the NARROWNESS above, which
    /// <c>OverwatchProposalFenceTests.Unfenced_Prose_StaysNull</c> pins: a shared extractor that scans for
    /// an embedded object would silently start manufacturing verdicts out of a judge thinking out loud.
    /// </para>
    /// <para>
    /// Note this is the opposite disposition from the no-verdict body logging in
    /// <c>Overwatch.RunDiagnoseAsync</c>, which is PERMANENT — see the note there. The two landed in the
    /// same change for the same issue and have different lifespans, so neither should be removed by
    /// association with the other.
    /// </para>
    /// </summary>
    private static string Unfence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        // Drop the opening fence line (```
        // or ```json / ```JSON / ```  json) whatever the info string says.
        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        string body = trimmed[(firstNewline + 1)..].TrimEnd();

        // Drop the closing fence if present. Absent is tolerated: a body truncated mid-stream still has its
        // opening fence, and a JSON object that happens to be complete should still parse.
        int lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? body[..lastFence].TrimEnd() : body;
    }

    private static OverwatchFixOp? ParseFix(JsonElement fix)
    {
        if (fix.ValueKind != JsonValueKind.Object
            || !fix.TryGetProperty("kind", out JsonElement kindEl)
            || kindEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string kind = kindEl.GetString() ?? "";
        switch (kind.Trim().ToLowerInvariant())
        {
            case "guidance":
                string? guidance = StringProp(fix, "guidance");
                return string.IsNullOrWhiteSpace(guidance)
                    ? null
                    : new OverwatchFixOp { Kind = OverwatchFixKind.GuidanceInjection, Guidance = guidance };

            case "budget":
                string? field = StringProp(fix, "field");
                int? value = fix.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int iv)
                    ? iv
                    : null;
                return string.IsNullOrWhiteSpace(field)
                    ? null
                    : new OverwatchFixOp { Kind = OverwatchFixKind.BudgetOverride, BudgetField = field, BudgetValue = value };

            case "file-edit":
                string? path = StringProp(fix, "path");
                return string.IsNullOrWhiteSpace(path)
                    ? null
                    : new OverwatchFixOp { Kind = OverwatchFixKind.FileEdit, TargetPath = path };

            case "task-field":
                string? taskField = StringProp(fix, "field");
                return string.IsNullOrWhiteSpace(taskField)
                    ? null
                    : new OverwatchFixOp { Kind = OverwatchFixKind.TaskFieldEdit, TaskField = taskField };

            default:
                return null;
        }
    }

    private static string? StringProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

/// <summary>The judge's structural read of a struggling task (doc 11 §1).</summary>
public enum OverwatchClassification
{
    /// <summary>More attempts (with a sanctioned change) could plausibly converge.</summary>
    Retryable,

    /// <summary>Structurally doomed — halt honestly with a precise diagnosis; grant nothing even on a TTY.</summary>
    Doomed
}
