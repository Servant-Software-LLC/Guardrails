using System.Text.Json.Serialization;

namespace Guardrails.Core.Journal;

/// <summary>
/// The on-disk shape of <c>state/run.json</c> (SSOT §7). Serialized with camelCase
/// property names and the SSOT's kebab-case status/outcome strings (via the converters in
/// <see cref="JournalJson"/>). All fields are present so the journal round-trips losslessly.
/// </summary>
public sealed record JournalDocument
{
    /// <summary>Schema version of <c>run.json</c> (SSOT §7: 1).</summary>
    public int Version { get; init; } = 1;

    /// <summary>Stable id for this run, e.g. <c>2026-06-10T16-22-31Z-a1b2</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>SHA-256 over guardrails.json + all task.json, prefixed <c>sha256:</c>. Mismatch on resume ⇒ warning.</summary>
    public required string PlanHash { get; init; }

    /// <summary>The next merge sequence to hand out (monotonic; SSOT §6.3 / §7).</summary>
    public long NextMergeSequence { get; init; } = 1;

    /// <summary>Per-task records, keyed by task id.</summary>
    public IReadOnlyDictionary<string, TaskJournalEntry> Tasks { get; init; } =
        new Dictionary<string, TaskJournalEntry>();
}

/// <summary>One task's journal record (SSOT §7 <c>tasks.&lt;id&gt;</c>).</summary>
public sealed record TaskJournalEntry
{
    /// <summary>Current status.</summary>
    public required TaskStatus Status { get; init; }

    /// <summary>The merge sequence assigned when this task's fragment merged; null until then.</summary>
    public long? MergeSequence { get; init; }

    /// <summary>Attempt records in attempt order (1-based).</summary>
    public IReadOnlyList<AttemptRecord> Attempts { get; init; } = [];
}

/// <summary>One attempt of one task (SSOT §7 attempt record).</summary>
public sealed record AttemptRecord
{
    /// <summary>1-based attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>UTC start time (ISO-8601).</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC end time (ISO-8601).</summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>The action's exit code (null if the action never ran).</summary>
    public int? ActionExitCode { get; init; }

    /// <summary>The attempt outcome.</summary>
    public required AttemptOutcome Outcome { get; init; }

    /// <summary>Failed guardrails (name + actionable reason); empty unless <see cref="Outcome"/> is guardrail-failed.</summary>
    public IReadOnlyList<FailedGuardrail> FailedGuardrails { get; init; } = [];

    /// <summary>Prompt-attempt total cost in USD; null for deterministic attempts (and all of M3).</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Path to this attempt's log dir, relative to the plan dir (SSOT §7/§8).</summary>
    public required string LogDir { get; init; }
}

/// <summary>A guardrail that failed, with its one-line reason (SSOT §7 <c>failedGuardrails</c>).</summary>
public sealed record FailedGuardrail
{
    /// <summary>The guardrail's name (filename minus extension).</summary>
    public required string Name { get; init; }

    /// <summary>The actionable failure reason (guardrail stdout, or a timeout/crash note).</summary>
    public required string Reason { get; init; }
}
