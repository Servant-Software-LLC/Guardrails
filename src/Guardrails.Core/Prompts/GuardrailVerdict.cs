using System.Text.Json;

namespace Guardrails.Core.Prompts;

/// <summary>
/// A prompt guardrail's verdict (SSOT §4.2): the JSON object the verifier writes to
/// <c>GUARDRAILS_VERDICT_OUT</c>. The harness judges pass/fail by this file alone — never
/// by the runner's exit code.
/// </summary>
public sealed record GuardrailVerdict
{
    /// <summary>Whether the guardrail passes.</summary>
    public required bool Pass { get; init; }

    /// <summary>The one-line reason (actionable on failure — it becomes retry feedback).</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Reads and validates a prompt guardrail's verdict file (SSOT §4.2). Missing file, invalid
/// JSON, a non-object, or a missing <c>pass</c> key all yield a FAIL with the contractual
/// reason "guardrail produced no valid verdict (see logs)". A valid verdict missing only the
/// <c>reason</c> is tolerated (reason defaults to empty), since <c>pass</c> is the decision.
/// </summary>
public static class GuardrailVerdictReader
{
    /// <summary>The reason used whenever a verdict cannot be read or is malformed.</summary>
    public const string NoValidVerdictReason = "guardrail produced no valid verdict (see logs)";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Read the verdict from the file at <paramref name="verdictPath"/>.</summary>
    public static GuardrailVerdict Read(string verdictPath)
    {
        if (!File.Exists(verdictPath))
        {
            return Fail(NoValidVerdictReason);
        }

        string raw;
        try
        {
            raw = File.ReadAllText(verdictPath);
        }
        catch (IOException)
        {
            return Fail(NoValidVerdictReason);
        }

        return Parse(raw);
    }

    /// <summary>Parse a verdict from raw JSON text (used by tests and <see cref="Read"/>).</summary>
    public static GuardrailVerdict Parse(string rawJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson, ParseOptions);
        }
        catch (JsonException)
        {
            return Fail(NoValidVerdictReason);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("pass", out JsonElement passElement) ||
                passElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Fail(NoValidVerdictReason);
            }

            bool pass = passElement.GetBoolean();
            string reason = root.TryGetProperty("reason", out JsonElement reasonElement) &&
                            reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString() ?? string.Empty
                : string.Empty;

            return new GuardrailVerdict { Pass = pass, Reason = reason };
        }
    }

    private static GuardrailVerdict Fail(string reason) => new() { Pass = false, Reason = reason };
}
