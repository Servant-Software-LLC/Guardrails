using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guardrails.Core.Journal;

/// <summary>
/// Appends the run-level autonomy detail stream to <c>logs/&lt;runId&gt;/autonomy.jsonl</c> (issue #361
/// Phase 3, doc 12 §6.1/§6.3) — the multi-fire DETAIL behind the durable <c>decisions[]</c> AUDIT, mirroring
/// the shipped overwatcher pattern (<c>decisions[]</c> + <c>overwatch.jsonl</c>) one level up. Because a gate
/// spans tasks AND waves (unlike the per-task <c>overwatch.jsonl</c>) the stream is run-level. An escalation,
/// a best-guess, AND a class-(b) retry each append exactly one compact single-line JSON record (§6.3), so the
/// file has one line per gate assessment.
/// </summary>
public static class AutonomyJsonl
{
    /// <summary>
    /// Compact, single-line camelCase serializer for the detail stream (contrast <see cref="JournalJson.Options"/>,
    /// which pretty-prints <c>run.json</c>). Omit-null so the gate-specific
    /// <c>question</c>/<c>bestGuess</c>/<c>rationale</c> and the hard-blocker-null
    /// <c>criticality</c>/<c>confidence</c> add no null noise. Mirrors <c>OverwatchDetailWriter</c>.
    /// </summary>
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Append one compact single-line JSON <paramref name="record"/> (§6.3 fields) to
    /// <c>&lt;logsDirectory&gt;/&lt;runId&gt;/autonomy.jsonl</c>, creating the run's log directory if needed.
    /// Append-only (never truncates): N calls ⇒ N lines, each a single object terminated by <c>\n</c>.
    /// </summary>
    public static void Append(string logsDirectory, string runId, AutonomyRecord record)
    {
        string runLogDir = Path.Combine(logsDirectory, runId);
        Directory.CreateDirectory(runLogDir);

        string line = JsonSerializer.Serialize(record, LineOptions);
        File.AppendAllText(Path.Combine(runLogDir, "autonomy.jsonl"), line + "\n", Utf8NoBom);
    }
}

/// <summary>
/// One <c>autonomy.jsonl</c> record (one gate assessment, doc 12 §6.3). Serialized as one compact
/// single-line camelCase JSON object, omit-null. Carries the classification/criticality/confidence/threshold
/// audit fields plus the gate-specific <c>question</c>/<c>bestGuess</c>/<c>rationale</c>.
/// </summary>
public sealed record AutonomyRecord
{
    /// <summary>UTC time the gate was assessed (ISO-8601).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The gate: <c>needs-human</c> · <c>wave-checkpoint</c> · <c>review-gate</c> · <c>blocker</c>.</summary>
    public required string Gate { get; init; }

    /// <summary>The <c>decisions[]</c> boundary this gate maps onto (<c>task</c> for a needs-human; <c>wave</c> for a checkpoint/review-gate).</summary>
    public required string Boundary { get; init; }

    /// <summary>The unit the gate concerned — a task id / wave dir.</summary>
    public required string Subject { get; init; }

    /// <summary>How the stop was classified (§4): <c>judgment-call</c> · <c>hard-blocker-retryable</c> · <c>hard-blocker-permanent</c>.</summary>
    public required string Classification { get; init; }

    /// <summary>How the gate resolved — a <see cref="DecisionTokens"/> value (<c>escalated</c>/<c>proceeded-best-guess</c>/…).</summary>
    public required string Decision { get; init; }

    /// <summary>The assessed criticality level (<c>low</c>·<c>moderate</c>·<c>high</c>·<c>critical</c>); null for a hard blocker.</summary>
    public string? Criticality { get; init; }

    /// <summary>The judge's confidence (<c>low</c>·<c>moderate</c>·<c>high</c>); null for a hard blocker.</summary>
    public string? Confidence { get; init; }

    /// <summary>The <c>escalationThreshold</c> in force at this gate (after any per-gate override).</summary>
    public string? Threshold { get; init; }

    /// <summary>Gate-specific: the human-answerable question (a <c>needs-human</c> gate).</summary>
    public string? Question { get; init; }

    /// <summary>Gate-specific: the recorded best-guess when the decision proceeded below threshold.</summary>
    public string? BestGuess { get; init; }

    /// <summary>Gate-specific: the rationale for the decision (why it was safe to proceed / why it escalated).</summary>
    public string? Rationale { get; init; }
}
