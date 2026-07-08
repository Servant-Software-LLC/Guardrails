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

    /// <summary>
    /// OPTIONAL, append-only, unified autonomy-policy decision log (SSOT §2.1/§7): one entry per decision
    /// boundary, <c>boundary</c>-discriminated (M1 emits only <c>drift</c> — a Part C safe-drift rewind's
    /// audit, whether prompted-<c>y</c>, <c>--autonomy auto</c>-authorized, or via the manual scoped
    /// <c>reset</c>; the <c>wave</c>/<c>task</c> boundaries append here in M2/M3). This is the canonical
    /// durable store (it replaces the pre-fold <c>driftResolutions[]</c> section). Additive and
    /// backward-compatible on the same terms as <see cref="PlanPreflights"/> — absent (not <c>null</c>
    /// noise) on a run that recorded no decision.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Execution.DecisionEntry>? Decisions { get; init; }
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

    /// <summary>
    /// The #175 merge-collision advisory (SSOT §3.3, issue #205): when the terminal gate fails and ≥2
    /// tasks have OVERLAPPING <c>writeScope</c> on a shared file, this names the offending task pair(s) +
    /// the shared path(s) so a human sees <i>"this looks like a merge collision between task A and task B
    /// on &lt;file&gt;"</i> rather than a bare build error. Structural + advisory (derived purely from the
    /// writeScope-overlap topology, never the compiler error text). OPTIONAL and additive — omitted (not
    /// null noise) when the gate passed or no two writeScopes overlap.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CollisionHint { get; init; }
}

/// <summary>One task's journal record (SSOT §7 <c>tasks.&lt;id&gt;</c>).</summary>
public sealed record TaskJournalEntry
{
    /// <summary>Current status.</summary>
    public required TaskStatus Status { get; init; }

    /// <summary>The merge sequence assigned when this task's fragment merged; null until then.</summary>
    public long? MergeSequence { get; init; }

    /// <summary>
    /// The task's <c>TaskDefinitionHash</c> (SSOT §7.2, issue #274 Part A) stamped at its most recent
    /// SUCCESSFUL settle: a <c>sha256:</c>-prefixed hash of <c>task.json</c> + the resolved action file +
    /// <c>guardrails/**</c> + <c>preflights/**</c>. On resume the harness recomputes the current hash and,
    /// if it no longer matches this recorded one, halts with a definition-drift report instead of silently
    /// reusing the stale cached segment. OPTIONAL and additive — a journal entry predating this field OMITS
    /// it (serialized only when non-null via <see cref="JsonIgnoreAttribute"/>); an absent recorded hash is
    /// treated as "unknown — assume unchanged" on resume, so an upgrade never forces a re-run storm.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionHash { get; init; }

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

    /// <summary>
    /// Per-attempt provenance the harness knows when it launches the attempt (issue #198): the resolved
    /// model the agent ran on, the segment worktree it wrote in, and the base commit it forked from.
    /// OPTIONAL and additive — a script attempt, a serial-mode attempt, or an older journal simply
    /// OMITS this section (serialized only when present via <see cref="JsonIgnoreAttribute"/>), so it is
    /// backward-compatible and never adds <c>null</c> noise to <c>run.json</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttemptProvenance? Provenance { get; init; }
}

/// <summary>
/// Per-attempt provenance recorded in <c>run.json</c> and mirrored to
/// <c>&lt;attempt&gt;/attempt-provenance.json</c> (SSOT §7/§8, issue #198): the facts the harness already
/// knows at attempt launch. Every field is optional so a script attempt (no model, serial mode with no
/// segment) records only what applies. It records WHAT ran WHERE without re-deriving it from logs.
/// </summary>
public sealed record AttemptProvenance
{
    /// <summary>
    /// The model the agent ran on — the FULLY RESOLVED <c>--model</c> (issue #200): the task.json
    /// <c>action.model</c> override when the task declares one, else the prompt-runner config's own
    /// <c>model</c>, else the sentinel <c>"(cli default)"</c> when neither is set. Null for a script
    /// attempt (no model).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    /// <summary>
    /// The segment worktree's git branch name (e.g. <c>guardrails/&lt;runId&gt;/&lt;task&gt;/attempt-1</c>).
    /// Null in serial mode (no per-task segment).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SegmentBranch { get; init; }

    /// <summary>The absolute segment worktree path this attempt wrote in. Null in serial mode.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorktreePath { get; init; }

    /// <summary>The base commit sha the segment forked from (<c>taskBase</c>). Null in serial mode.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseCommit { get; init; }
}

/// <summary>A guardrail that failed, with its one-line reason (SSOT §7 <c>failedGuardrails</c>).</summary>
public sealed record FailedGuardrail
{
    /// <summary>The guardrail's name (filename minus extension).</summary>
    public required string Name { get; init; }

    /// <summary>The actionable failure reason (guardrail stdout, or a timeout/crash note).</summary>
    public required string Reason { get; init; }
}
