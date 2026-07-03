namespace Guardrails.Core.Model;

/// <summary>
/// The plan's run configuration — the deserialized <c>guardrails.json</c> (SSOT §2),
/// with documented defaults applied. Only the fields M2 exercises are modelled
/// concretely; <see cref="Interpreters"/> and <see cref="PromptRunnerNames"/> carry
/// enough to validate and run a script-only plan.
/// </summary>
public sealed record RunConfig
{
    /// <summary>Schema version of <c>guardrails.json</c> (required field; SSOT §2).</summary>
    public required int Version { get; init; }

    /// <summary>Worker count for the scheduler. Default 3 (SSOT §2).</summary>
    public int MaxParallelism { get; init; } = 3;

    /// <summary>Retries after the first attempt. Default 2. (Retry is M4; not honored in M2.)</summary>
    public int DefaultRetries { get; init; } = 2;

    /// <summary>
    /// Optional per-run cost ceiling in USD (SSOT §2). When set, the scheduler stops launching
    /// new attempts once the journal's cumulative cost reaches or exceeds it, settling the
    /// remaining work <c>needs-human</c> ("cost cap reached"). Null (the default) means no cap —
    /// today's behavior, unchanged. A present non-positive value is a validation error
    /// (<see cref="Loading.DiagnosticCodes.CostCapNonPositive"/>).
    /// </summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>Per-attempt timeout ceiling when nothing narrower applies. Default 1800s.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 1800;

    /// <summary>
    /// The cumulative wall-clock budget (seconds) a task may spend PAUSED on transient,
    /// retryable infrastructure conditions before the harness gives up (SSOT §2/§9, issue #115).
    /// A transient signal (HTTP 429/503/529, "overloaded", a usage/session/rate limit) does NOT
    /// consume the retry budget: the harness backs off (bounded exponential) and re-runs the same
    /// attempt. This is the named bound on "a rate limit must never mark needs-human" — only if the
    /// limit fails to clear within this whole-task budget does the task settle <c>needs-human</c>
    /// with a distinct rate-limit reason ("re-run later"). Default 1800s (30 min). A non-positive
    /// value disables pausing (a transient signal is then treated as a normal action failure).
    /// Default 14400s (4h) — a long unattended/overnight run must ride out a provider outage or a
    /// multi-hour usage-limit window without settling <c>needs-human</c> (issue #189).
    /// </summary>
    public int TransientPauseBudgetSeconds { get; init; } = 14400;

    /// <summary>How guardrail failures are handled within an attempt. Default <see cref="GuardrailMode.FailFast"/>.</summary>
    public GuardrailMode GuardrailMode { get; init; } = GuardrailMode.FailFast;

    /// <summary>cwd for all child processes, relative to the plan dir. Default "..".</summary>
    public string Workspace { get; init; } = "..";

    /// <summary>Interpreter overrides/extensions from <c>guardrails.json</c> (SSOT §5.2). Keyed by extension (".ps1").</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Interpreters { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Names declared under <c>promptRunners</c> (excluding the "default" pointer).
    /// Used to validate runner references on tasks. Kept consistent with
    /// <see cref="PromptRunners"/> (it is exactly its key set).
    /// </summary>
    public IReadOnlySet<string> PromptRunnerNames { get; init; } = new HashSet<string>();

    /// <summary>
    /// Root directory under which per-run worktrees are created (SSOT §2, plan 08 §1).
    /// Null (the default) means the harness picks a managed path under TEMP.
    /// Field exists for the model contract; the loader does not yet read it from JSON (M2).
    /// </summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>
    /// When true, agent tasks run against the current branch rather than a plan branch (SSOT §2).
    /// Default false. Field exists for the model contract; the loader does not yet read it from JSON (M2).
    /// </summary>
    public bool RunOnCurrentBranch { get; init; }

    /// <summary>
    /// When true, the harness auto-merges each segment worktree back on success (SSOT §5.3).
    /// Default false. Field exists for the model contract; the loader does not yet read it from JSON (M2).
    /// </summary>
    public bool MergeOnSuccess { get; init; }

    /// <summary>
    /// Opt-in auto-file of the needs-human triage GH issue (SSOT §9, plan 08 Decision 8). Default
    /// false — triage only DRAFTS the issue into <c>feedback.md</c> and files nothing to a remote.
    /// When true (and gated behind a configured GH repo + token) the harness auto-files; flows to
    /// <see cref="Execution.NeedsHumanTriage"/>'s <c>autoFile</c> argument.
    /// </summary>
    public bool TriageAutoFile { get; init; }

    /// <summary>The value of <c>promptRunners.default</c>, if present.</summary>
    public string? DefaultPromptRunner { get; init; }

    /// <summary>
    /// The full prompt-runner configurations (SSOT §2/§9), keyed by runner name. Empty for a
    /// script-only plan; a plan with prompt tasks but an empty map is a validation error
    /// (GR2008). The key set equals <see cref="PromptRunnerNames"/>.
    /// </summary>
    public IReadOnlyDictionary<string, PromptRunnerConfig> PromptRunners { get; init; } =
        new Dictionary<string, PromptRunnerConfig>();
}
