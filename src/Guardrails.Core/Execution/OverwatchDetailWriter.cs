using System.Text;
using System.Text.Json;

namespace Guardrails.Core.Execution;

/// <summary>
/// Appends the overwatcher's per-fire detail stream to <c>logs/&lt;runId&gt;/&lt;task-id&gt;/overwatch.jsonl</c>
/// (SSOT §8, doc 11 §7/§8, #305 Decision D). Because the overwatcher may fire MULTIPLE times per task
/// (unlike the single terminal <c>triage.json</c>), this append-only stream carries the multi-fire detail —
/// one record per decision with the trigger, classification, the proposed fix ops AND the authority class
/// the mechanical classifier assigned each, and what (if anything) was applied. The durable AUDIT is the
/// shared <c>decisions[]</c>; this is the diagnostic detail. Best-effort: a logs-tree write hiccup is
/// swallowed — the overwatcher is advisory and must never affect the run.
/// </summary>
internal static class OverwatchDetailWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Append one decision record to <c>&lt;taskLogDir&gt;/overwatch.jsonl</c> (one compact JSON object per line).</summary>
    public static void Append(string taskLogDir, OverwatchDetailRecord record)
    {
        try
        {
            Directory.CreateDirectory(taskLogDir);
            string line = JsonSerializer.Serialize(record, Options);
            File.AppendAllText(Path.Combine(taskLogDir, "overwatch.jsonl"), line + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Advisory detail stream — a write hiccup must never affect the run.
        }
    }
}

/// <summary>One <c>overwatch.jsonl</c> record (one overwatcher fire). Serialized camelCase, omit-null.</summary>
internal sealed record OverwatchDetailRecord
{
    public required string At { get; init; }
    public required string Trigger { get; init; }
    public required int Attempt { get; init; }
    public required string Policy { get; init; }

    /// <summary>How it resolved: <c>halted</c> / <c>prompted-approved</c> / <c>prompted-declined</c> / <c>no-action</c>.</summary>
    public required string Decision { get; init; }

    /// <summary><c>retryable</c> / <c>doomed</c>; null when no proposal parsed.</summary>
    public string? Classification { get; init; }

    public string? Diagnosis { get; init; }

    /// <summary>Each proposed fix op with the authority class the mechanical classifier assigned it.</summary>
    public IReadOnlyList<OverwatchDetailFix> Fixes { get; init; } = [];

    /// <summary>Non-null on a grant: what was actually applied.</summary>
    public OverwatchDetailApplied? Applied { get; init; }

    public string? Headline { get; init; }
}

/// <summary>A proposed fix op + its classifier verdict, for the detail stream.</summary>
internal sealed record OverwatchDetailFix
{
    public required string Kind { get; init; }
    public required string Authority { get; init; }
    public string? Target { get; init; }
}

/// <summary>What a grant applied.</summary>
internal sealed record OverwatchDetailApplied
{
    public bool Guidance { get; init; }
    public int ExtraRetries { get; init; }
}
