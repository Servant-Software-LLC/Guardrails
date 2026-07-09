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
            using JsonDocument doc = JsonDocument.Parse(resultText);
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
