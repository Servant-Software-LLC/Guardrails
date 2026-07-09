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
    /// When true (the DEFAULT, #340 — "green means delivered"), a wholly-green run delivers the plan
    /// branch <c>guardrails/&lt;plan-name&gt;</c> into the user's original branch at run end (SSOT §5.3 / §2).
    /// The merge-back is FF-when-possible, else a real merge whose deterministic re-verify must pass;
    /// <b>AI-merge is withheld at this boundary</b> — a conflict / failed re-verify / dirty user tree halts
    /// (exit 2) with the plan branch intact (a merge, not a move, so the branch survives). Opt out with
    /// <c>"mergeOnSuccess": false</c> or the CLI <c>--no-merge-on-success</c>. The default flipped ON because
    /// the merge-back is already non-destructive and halts loudly on any obstacle, so delivering by default
    /// aligns the success signal with reality without the risks the old OFF default guarded against.
    /// </summary>
    public bool MergeOnSuccess { get; init; } = true;

    /// <summary>
    /// The RAW <c>mergeOnSuccess</c> value exactly as it appeared in <c>guardrails.json</c> (SSOT §2):
    /// <c>null</c> when the key was OMITTED (so the effective <see cref="MergeOnSuccess"/> came from the
    /// #340 <c>true</c> default), or <c>true</c>/<c>false</c> when explicitly set. Not a JSON field of its
    /// own — it preserves the raw manifest's nullability so the CLI can distinguish "omitted → defaulted on"
    /// from an explicit value and print the one-time "delivered by default" notice ONLY in the former case.
    /// The RESOLVED behavior is always <see cref="MergeOnSuccess"/>.
    /// </summary>
    public bool? MergeOnSuccessExplicit { get; init; }

    /// <summary>
    /// Opt-in auto-file of the needs-human triage GH issue (SSOT §9, plan 08 Decision 8). Default
    /// false — triage only DRAFTS the issue into <c>feedback.md</c> and files nothing to a remote.
    /// When true (and gated behind a configured GH repo + token) the harness auto-files; flows to
    /// <see cref="Execution.NeedsHumanTriage"/>'s <c>autoFile</c> argument.
    /// </summary>
    public bool TriageAutoFile { get; init; }

    /// <summary>
    /// The unified autonomy knob (SSOT §2.1, #254/#269/#274): how the harness handles EVERY prompt/halt/auto
    /// decision boundary. Default <see cref="Model.AutonomyPolicy.Prompt"/>: prompt in an interactive TTY,
    /// halt when non-interactive. <see cref="Model.AutonomyPolicy.Auto"/> (CLI <c>--autonomy auto</c>, or the
    /// legacy alias <c>--reprocess-drift</c>) applies a SAFE decision with no prompt;
    /// <see cref="Model.AutonomyPolicy.Halt"/> always halts. In M1 the only wired boundary is the resume
    /// definition-drift gate (§7.2); an UNSAFE drift always halts regardless of this field. An unrecognized
    /// JSON value is a validation error (<see cref="Loading.DiagnosticCodes.InvalidAutonomyPolicy"/>).
    /// </summary>
    public AutonomyPolicy AutonomyPolicy { get; init; } = AutonomyPolicy.Prompt;

    /// <summary>The value of <c>promptRunners.default</c>, if present.</summary>
    public string? DefaultPromptRunner { get; init; }

    /// <summary>
    /// When true (the default), a worktree-mode NON-FINAL retry rollback STASHES the attempt's full
    /// working-tree state (including uncommitted writes) to an inspectable git ref
    /// (<c>refs/guardrails/&lt;taskId&gt;/attempt-&lt;N&gt;</c>) plus a directly-applyable patch file BEFORE the
    /// existing <c>git reset --hard &lt;taskBase&gt; + git clean -fd</c> rollback runs (issues #195 / #306).
    /// The next attempt still starts from the clean <c>taskBase</c> — this does NOT change — but its retry
    /// feedback exposes the stash as a first-class, agent-controlled input: a <c>git diff --stat</c>
    /// summary, the applyable patch (<c>git apply</c> for ALL the prior work), and the ref
    /// (<c>git checkout &lt;ref&gt; -- &lt;path&gt;</c> for SOME), so the agent can pull all/some/none instead of
    /// re-deriving from a summary. <b>Issue #306</b> extends this beyond #195's original non-logic
    /// outcomes: salvage now fires for EVERY non-final worktree failure kind — guardrail-fail, action-fail,
    /// timeout, max-turns, output-cap, write-scope — because the agent (informed by the per-guardrail
    /// verdicts), not the harness, decides how much to reuse; the clean-slate reset remains the default
    /// starting point. Set this false to disable salvage entirely. No-op in serial mode (no segment
    /// worktree to stash) and on the final attempt (never reset). Fragment-rejection paths are the one
    /// documented exception (they keep the #162 re-author disclosure and are not stashed).
    /// </summary>
    public bool PreserveAttemptsForSalvage { get; init; } = true;

    /// <summary>
    /// The full prompt-runner configurations (SSOT §2/§9), keyed by runner name. Empty for a
    /// script-only plan; a plan with prompt tasks but an empty map is a validation error
    /// (GR2008). The key set equals <see cref="PromptRunnerNames"/>.
    /// </summary>
    public IReadOnlyDictionary<string, PromptRunnerConfig> PromptRunners { get; init; } =
        new Dictionary<string, PromptRunnerConfig>();
}
