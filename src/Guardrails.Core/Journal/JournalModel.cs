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

    /// <summary>
    /// OPTIONAL top-level pre-DAG preflight phase result (SSOT §7, the two-scope preflights F9 split). The
    /// pre-DAG phase runs BEFORE any task is scheduled; a failure halts the run (exit 2). Additive and
    /// backward-compatible: a plan WITHOUT the feature OMITS this section (an older reader ignores it), so
    /// it is written only when present — never serialized as <c>null</c> noise (see the
    /// <see cref="JsonIgnoreAttribute"/>). The existing <see cref="Tasks"/> shape is untouched.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanPreflightsSection? PlanPreflights { get; init; }

    /// <summary>
    /// OPTIONAL top-level terminal plan-guardrail gate result (SSOT §7, F9): the terminal
    /// <c>&lt;plan&gt;/guardrails/</c> gate evaluated on the merged plan-branch HEAD; a failure halts the
    /// run (exit 2). Additive and backward-compatible on the same terms as <see cref="PlanPreflights"/> —
    /// absent (not null) on a plan without the feature.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanGuardrailsSection? PlanGuardrails { get; init; }
}

/// <summary>
/// The pre-DAG preflight phase result (SSOT §7 top-level <c>planPreflights</c>, two-scope preflights F9
/// split). <c>planHash</c>-keyed so it self-scopes to the plan it evaluated.
/// </summary>
public sealed record PlanPreflightsSection
{
    /// <summary>The phase status (<c>passed</c> or <c>plan-preflight-failed</c>).</summary>
    public required PlanPhaseStatus Status { get; init; }

    /// <summary>The plan hash the preflight phase evaluated against (SSOT §7; mirrors <see cref="JournalDocument.PlanHash"/>).</summary>
    public required string PlanHash { get; init; }

    /// <summary>UTC time the preflight phase was evaluated (ISO-8601).</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>The individual preflight checks that ran (name + pass/fail + optional reason).</summary>
    public IReadOnlyList<PlanPreflightCheck> Checks { get; init; } = [];
}

/// <summary>One pre-DAG preflight check result (SSOT §7 <c>planPreflights.checks[]</c>).</summary>
public sealed record PlanPreflightCheck
{
    /// <summary>The check's name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the check passed.</summary>
    public required bool Passed { get; init; }

    /// <summary>The actionable failure reason; omitted when the check passed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>
/// The terminal plan-guardrail gate result on the merged plan-branch HEAD (SSOT §7 top-level
/// <c>planGuardrails</c>, two-scope preflights F9 split). <c>planHash</c>-keyed.
/// </summary>
public sealed record PlanGuardrailsSection
{
    /// <summary>The gate status (<c>passed</c> or <c>plan-guardrail-failed</c>).</summary>
    public required PlanPhaseStatus Status { get; init; }

    /// <summary>The plan hash the terminal gate evaluated against (SSOT §7).</summary>
    public required string PlanHash { get; init; }

    /// <summary>The guardrail checks that failed (name + reason); empty unless <see cref="Status"/> is plan-guardrail-failed.</summary>
    public IReadOnlyList<FailedGuardrail> FailedChecks { get; init; } = [];
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
