namespace Guardrails.Core.Execution;

/// <summary>The result of running a single guardrail.</summary>
public sealed record GuardrailResult
{
    public required string Name { get; init; }
    public required bool Passed { get; init; }

    /// <summary>One-line actionable reason on failure (the guardrail's first stdout line), else null.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The guardrail's full captured output on failure (stdout, or stderr when stdout is empty),
    /// for the retry feedback (issue #26 Gap 1). The one-line <see cref="Reason"/> truncates at
    /// the first line, which hid 8-of-9 build errors in a real failure; the retry agent needs
    /// every error, not just the first. Null for passing guardrails and prompt guardrails (whose
    /// signal is the one-line verdict reason).
    /// </summary>
    public string? Output { get; init; }
}

/// <summary>The full result of a single task in an M2 serial run.</summary>
public sealed record TaskResult
{
    public required string TaskId { get; init; }
    public required TaskOutcome Outcome { get; init; }

    /// <summary>The action's exit code, or null when the task was blocked and never ran.</summary>
    public int? ActionExitCode { get; init; }

    /// <summary>Guardrail results in execution order (empty if action failed or task was blocked).</summary>
    public IReadOnlyList<GuardrailResult> Guardrails { get; init; } = [];

    /// <summary>A short human-readable explanation of the outcome (for the summary and logs).</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// In worktree mode, the path to the validated fragment file for deferred B1 settle in the
    /// Scheduler. Null in serial mode (AttemptJournaler handles the merge immediately).
    /// </summary>
    public string? FragmentPath { get; init; }

    /// <summary>
    /// True when the Scheduler must perform the B1 deferred settle (fragment merge → git commit →
    /// journal RecordSettle) for this result. Set by <see cref="AttemptJournaler.ValidateFragmentForSettle"/>
    /// in worktree mode. False in serial mode (AttemptJournaler already merged and journaled).
    /// </summary>
    public bool DeferredSettle { get; init; }

    /// <summary>
    /// In worktree mode, the not-yet-journaled attempt data the Scheduler's B1 settle records (issue
    /// #196): a succeeded worktree task must journal a real <see cref="Journal.AttemptRecord"/> — with
    /// the same shape serial mode records — TOGETHER with the reserved <c>mergeSequence</c>, so
    /// <c>journal.Tasks[id].Attempts</c> is non-empty for a succeeded task in BOTH modes (SSOT §7).
    /// The executor computes this per-attempt data (attempt number, timing, cost, relative log dir,
    /// and the #198 provenance) but cannot record it, because the settle (and thus the outcome +
    /// mergeSequence) is deferred to the Scheduler under the integration lock. Null in serial mode
    /// (AttemptJournaler already recorded the attempt) and for non-deferred results.
    /// </summary>
    public PendingAttempt? PendingAttempt { get; init; }

    /// <summary>True only for a genuine success this run (not a resume skip).</summary>
    public bool Succeeded => Outcome == TaskOutcome.Succeeded;

    /// <summary>
    /// True when this task is "green" for the run's overall verdict: it succeeded this run
    /// or was skipped because the journal already recorded it as succeeded (resume).
    /// </summary>
    public bool IsGreen => Outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped;
}

/// <summary>
/// The per-attempt data a worktree-mode success carries to the Scheduler's B1 settle so it can journal
/// a real <see cref="Journal.AttemptRecord"/> alongside the reserved <c>mergeSequence</c> (issue #196).
/// The executor computes all of it during the attempt but defers the actual record because the outcome
/// (succeeded vs a non-FF-union rollback to needs-human) and the mergeSequence are only known after the
/// integration commit, under the integration lock. The settle path builds the AttemptRecord from these
/// fields — the SAME shape serial mode's <see cref="AttemptJournaler.CompleteSucceededOrInvalidFragment"/>
/// records — so a succeeded task has a populated <c>Attempts</c> list in BOTH modes.
/// </summary>
public sealed record PendingAttempt
{
    /// <summary>1-based attempt number (already reserved by the journal for this attempt's log dir).</summary>
    public required int Attempt { get; init; }

    /// <summary>UTC attempt start time.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The action's exit code (0 on the success path).</summary>
    public int? ActionExitCode { get; init; }

    /// <summary>The prompt attempt's total cost in USD (null for a script or an unreported prompt cost).</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>This attempt's log dir, relative to the plan dir (SSOT §7/§8).</summary>
    public required string LogDir { get; init; }

    /// <summary>
    /// The #198 provenance the harness knew at attempt launch (resolved model, segment worktree,
    /// base commit). Never null in worktree mode (the segment always exists); the model is null for
    /// a script task.
    /// </summary>
    public Journal.AttemptProvenance? Provenance { get; init; }
}

/// <summary>The aggregate result of an entire run.</summary>
public sealed record RunReport
{
    /// <summary>Per-task results in plan order.</summary>
    public required IReadOnlyList<TaskResult> Tasks { get; init; }

    /// <summary>True when the run was cancelled (Ctrl+C) before quiescence.</summary>
    public bool Cancelled { get; init; }

    /// <summary>
    /// True when every task is green (succeeded this run or skipped as already-succeeded) AND the run did
    /// not HALT at a run-level boundary. The halt guards matter for a WAVED plan whose halt leaves no
    /// non-green task in the report — e.g. an unauthored next wave (SSOT §14.4) contributes zero tasks, so
    /// a plain per-task check would read "all succeeded" for a run that actually stopped. A halted /
    /// aborted / definition-drifted run is never "all succeeded".
    /// </summary>
    public bool AllSucceeded => !HasDefinitionDrift && !HasWaveHalt && !Aborted && Tasks.All(t => t.IsGreen);

    /// <summary>True when at least one task failed or was blocked.</summary>
    public bool AnyFailed => Tasks.Any(t => !t.IsGreen);

    /// <summary>
    /// The outcome of the end-of-run merge-on-success delivery (plan 08 SSOT §5.3).
    /// Null when <c>mergeOnSuccess</c> is false or the run was not wholly green.
    /// Implemented by task 22.
    /// </summary>
    public MergeOnSuccessResult? MergeOnSuccessOutcome { get; init; }

    /// <summary>
    /// Free-text detail for the merge-on-success outcome when it carries one — specifically the git
    /// hook's stderr when <see cref="MergeOnSuccessOutcome"/> is
    /// <see cref="MergeOnSuccessResult.HookRejected"/> (issues #149/#150). Null otherwise. The CLI
    /// renders this verbatim so the user sees exactly why their hook rejected the user-branch merge.
    /// </summary>
    public string? MergeOnSuccessDetail { get; init; }

    /// <summary>
    /// The user's original branch name a wholly-green run's work was DELIVERED to (issue #340), set by the
    /// Scheduler's <c>Finalize</c> only when the end-of-run merge-back actually ran and succeeded
    /// (<see cref="MergeOnSuccessOutcome"/> is <see cref="MergeOnSuccessResult.FastForwarded"/> or
    /// <see cref="MergeOnSuccessResult.Merged"/>); null otherwise. Purely descriptive — it does NOT change
    /// the delivery gate or the exit code. The CLI uses it to NAME the branch in the one-time
    /// "delivered by default" notice printed when delivery fired purely because of the new #340 default.
    /// </summary>
    public string? DeliveredToBranch { get; init; }

    /// <summary>
    /// True when this run drained WHOLLY GREEN (the DAG) but the completed work was NOT delivered to the
    /// user's branch because <c>mergeOnSuccess</c> resolved <b>false</b> (issue #340). The verified work
    /// is sitting on the plan branch <c>guardrails/&lt;plan-name&gt;</c>, undelivered — one
    /// <c>--fresh</c>/<c>reset -y</c> away from destruction. Set by the Scheduler's <c>Finalize</c> ONLY
    /// when a real, SEPARATE plan branch exists (worktree mode: a worktree provider AND an integration
    /// handle are present, and the run is not <c>runOnCurrentBranch</c>) so it is HONEST: in serial mode
    /// there is no plan branch, and in <c>runOnCurrentBranch</c> mode the plan branch IS the user's
    /// current branch — the work already lives in the user's checkout, nothing is undelivered — so this
    /// stays false. It is also false whenever delivery actually RAN (that requires <c>mergeOnSuccess</c>
    /// true, so <see cref="MergeOnSuccessOutcome"/> is then non-null and this false — never both). The CLI
    /// renders a loud, unmissable warning when this is true AND the terminal gate also passed (the warning
    /// belongs behind the CLI seam, SSOT §7).
    /// </summary>
    public bool WhollyGreenButUndelivered { get; init; }

    /// <summary>
    /// Non-null when the run was ABORTED by an unexpected infrastructure fault (a task executor or an
    /// integration step threw — e.g. an offline git hook failing an INTERNAL commit, or git itself
    /// being unavailable). Rather than propagating an unhandled exception out of the scheduler
    /// (issue #150), the run terminates the worker pool, runs the end-of-run cleanup sweep, and
    /// returns a report carrying this reason. The CLI renders a one-line diagnostic + remedy, writes
    /// the FULL exception to the run logs, and exits non-zero — an honest halt, never a raw stack
    /// trace as the headline. When set, treat the run as failed regardless of per-task outcomes.
    /// </summary>
    public RunAbort? Abort { get; init; }

    /// <summary>True when the run was aborted by an infrastructure fault (see <see cref="Abort"/>).</summary>
    public bool Aborted => Abort is not null;

    /// <summary>
    /// Non-null when the resume pre-pass HALTED the run because at least one already-<c>succeeded</c>
    /// task's current <c>TaskDefinitionHash</c> no longer matches the hash recorded at its last
    /// successful settle (SSOT §7.2, issue #274 Part A). The harness scheduled NOTHING — it neither
    /// silently reused the stale cached segment nor silently re-ran the changed task. A pre-DAG halt, a
    /// sibling of <see cref="Abort"/>; the CLI renders it where <see cref="Abort"/> renders and exits
    /// <b>2</b> (actionable/needs-human, matching planPreflights/planGuardrails), NOT 1. When set, treat
    /// the run as halted regardless of per-task outcomes.
    /// </summary>
    public DefinitionDriftReport? DefinitionDrift { get; init; }

    /// <summary>True when the run halted on a definition-drift (see <see cref="DefinitionDrift"/>).</summary>
    public bool HasDefinitionDrift => DefinitionDrift is not null;

    /// <summary>
    /// Non-null when the pre-DAG gate recorded an autonomy-policy decision this run (SSOT §2.1/§7): in M1
    /// this is a Part C safe definition-drift AUTO-RESOLVED (§7.2) — the plan branch was rewound past the
    /// safe drifted suffix and its tasks journal-reset to re-run. This is NOT a halt — the run proceeds and
    /// returns the normal exit code (0 green / 2 needs-human); it carries the <c>drift</c>-boundary
    /// <see cref="DecisionEntry"/> for the end-of-run summary, mirroring the durable <c>decisions[]</c>
    /// journal section.
    /// </summary>
    public DecisionEntry? Decision { get; init; }

    /// <summary>
    /// Non-null when a WAVED run HALTED at a wave boundary (SSOT §14, #254 M2b) other than a per-task
    /// needs-human (which is reported via the ordinary task outcomes + later-wave Blocked entries): the next
    /// wave is unauthored/empty (the JIT checkpoint, §14.4), a wave's entry or exit gate failed, or a
    /// completed wave DRIFTED under a <c>halt</c>/unconfirmed-<c>prompt</c> policy (§14.6). The CLI renders
    /// it and exits <b>2</b> (actionable), like <see cref="DefinitionDrift"/>. When set, treat the run as
    /// halted regardless of per-task outcomes.
    /// </summary>
    public WaveHalt? WaveHalt { get; init; }

    /// <summary>True when the run halted at a wave boundary (see <see cref="WaveHalt"/>).</summary>
    public bool HasWaveHalt => WaveHalt is not null;
}

/// <summary>The kind of wave-boundary halt a WAVED run stopped at (SSOT §14, #254 M2b).</summary>
public enum WaveHaltKind
{
    /// <summary>The next wave folder is present but has no authored tasks (or is unauthored) — the human JIT-breakdown checkpoint (§14.4).</summary>
    NextWaveUnauthored,

    /// <summary>A completed wave's <c>WaveDefinitionHash</c> drifted and the policy did not authorize an auto-resolve (§14.6).</summary>
    WaveDrift,

    /// <summary>A wave's ENTRY preflight gate failed (§14.3) — the prior wave's outputs were not materialized as expected.</summary>
    EntryGateFailed,

    /// <summary>A wave's EXIT/terminal gate failed (§14.3) on the merged HEAD-so-far.</summary>
    ExitGateFailed
}

/// <summary>
/// A WAVED run's wave-boundary halt (SSOT §14, #254 M2b) — the wave-level analogue of
/// <see cref="DefinitionDriftReport"/>/<see cref="RunAbort"/>. Carries what the CLI renders + the exit-2
/// actionable next step.
/// </summary>
public sealed record WaveHalt
{
    /// <summary>The wave directory the run halted at (e.g. <c>wave-02-build</c>).</summary>
    public required string WaveDir { get; init; }

    /// <summary>Which kind of wave-boundary halt this is.</summary>
    public required WaveHaltKind Kind { get; init; }

    /// <summary>One-line, human-readable headline for the console.</summary>
    public required string Headline { get; init; }

    /// <summary>Fuller detail / remediation (may be empty).</summary>
    public string Detail { get; init; } = "";

    /// <summary>The integration worktree path a human breaks the next wave down against (JIT checkpoint, §14.4/decision D); null when N/A.</summary>
    public string? IntegrationWorktreePath { get; init; }

    /// <summary>For a wave-drift halt: this wave + its downstream waves that would re-run on resolve; empty otherwise.</summary>
    public IReadOnlyList<string> AffectedWaves { get; init; } = [];

    /// <summary>For a wave-drift halt: the recorded → current <c>WaveDefinitionHash</c>; null otherwise.</summary>
    public string? OldHash { get; init; }

    /// <summary>For a wave-drift halt: the current <c>WaveDefinitionHash</c>; null otherwise.</summary>
    public string? NewHash { get; init; }

    /// <summary>For a gate-failure halt: the failing gate checks (name + reason); empty otherwise.</summary>
    public IReadOnlyList<GuardrailResult> FailedGates { get; init; } = [];
}

/// <summary>
/// The issue #274 Part A definition-drift halt (SSOT §7.2): every already-succeeded task whose
/// definition changed since it last succeeded, reported for the human's decision rather than silently
/// re-executed (auto-invalidating a fan-in descendant would fork it from a base still carrying its own
/// stale commit — the exact bug one level down — so auto-invalidation is unsound; that soundness limit
/// is why Part A halts).
/// </summary>
public sealed record DefinitionDriftReport
{
    /// <summary>The drifted tasks, in plan order.</summary>
    public required IReadOnlyList<DriftedTask> Tasks { get; init; }

    /// <summary>
    /// Whether the drift COULD be auto-resolved (issue #274 Part C): <c>true</c> when the drifted set forms
    /// a provably-safe trailing suffix (so the halt is a policy/consent choice — the operator can re-run
    /// interactively or with <c>--reprocess-drift</c>); <c>false</c> when the rewind was REFUSED as unsound
    /// (a non-suffix / uncontained fan-in / trailer-less commit — no flag authorizes it, steer to the full
    /// <c>reset -y</c> rebuild). Lets the CLI print the RIGHT remediation instead of leading with a flag
    /// that would just re-halt. Defaults <c>true</c> (the Part A halt, before Part C evaluated safety, is a
    /// "human decides" halt).
    /// </summary>
    public bool SafeToAutoResolve { get; init; } = true;

    /// <summary>When <see cref="SafeToAutoResolve"/> is false, WHY the rewind was refused (the <see cref="SafeSuffixDecision.Refusal"/>); null otherwise.</summary>
    public string? RewindRefusal { get; init; }

    /// <summary>When <see cref="SafeToAutoResolve"/> is false, the out-of-set task that blocked the rewind (the <see cref="SafeSuffixDecision.BlockingTask"/>); null otherwise.</summary>
    public string? RewindBlockingTask { get; init; }
}

/// <summary>One task whose <c>TaskDefinitionHash</c> drifted since its last successful settle (§7.2).</summary>
public sealed record DriftedTask
{
    /// <summary>The drifted task's id.</summary>
    public required string TaskId { get; init; }

    /// <summary>The <c>sha256:</c>-prefixed definition hash recorded at the last successful settle.</summary>
    public required string OldHash { get; init; }

    /// <summary>The <c>sha256:</c>-prefixed definition hash of the current on-disk definition.</summary>
    public required string NewHash { get; init; }

    /// <summary>
    /// The plan-branch commit bearing this task's <c>Guardrails-Task-Hash:</c> trailer (§5.3) — the
    /// anchor the Tier-2 per-file breakdown recovers old bytes from. Null when unavailable (serial mode,
    /// a journal-only success with no plan-branch commit) — Tier 2 then degrades, Tier 1 stands.
    /// </summary>
    public string? OldCommit { get; init; }

    /// <summary>
    /// The Tier-2 per-file breakdown of which definition files drifted (best-effort). Empty when the
    /// old bytes were not recoverable from <see cref="OldCommit"/> — see <see cref="Note"/>.
    /// </summary>
    public IReadOnlyList<ChangedDefinitionFile> ChangedFiles { get; init; } = [];

    /// <summary>The reference command <c>git diff &lt;oldCommit&gt;..HEAD -- &lt;task paths&gt;</c> for full content.</summary>
    public required string DiffCommand { get; init; }

    /// <summary>
    /// The task's transitive-descendant set (<c>DependencyGraph.TransitiveDependentsOf</c>, full DAG
    /// closure) — a changed producer can change a consumer's inputs. Reported for the human's decision,
    /// not re-executed.
    /// </summary>
    public IReadOnlyList<string> Dependents { get; init; } = [];

    /// <summary>
    /// A Tier-2 degradation note when the prior file bytes were not recoverable from git (e.g. the plan
    /// folder was uncommitted at <see cref="OldCommit"/>, or there is no plan-branch commit at all);
    /// null when the full per-file breakdown is present. Tier 1 (the aggregate hash drift) never depends
    /// on git recovery.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>One drifted definition file in a <see cref="DriftedTask"/>'s Tier-2 breakdown (§7.2).</summary>
public sealed record ChangedDefinitionFile
{
    /// <summary>The file's path relative to the task folder (e.g. <c>guardrails/03-covers.ps1</c>, <c>action.prompt.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>How it drifted: <c>added</c>, <c>removed</c>, or <c>modified</c>.</summary>
    public required string Change { get; init; }

    /// <summary>Lines added (approximate line-multiset delta); null when not a modification/addition.</summary>
    public int? Added { get; init; }

    /// <summary>Lines removed (approximate line-multiset delta); null when not a modification/removal.</summary>
    public int? Removed { get; init; }
}

/// <summary>
/// Carries why a run was aborted by an unexpected infrastructure fault (issue #150). The
/// <see cref="Headline"/> is the one-line human diagnostic the CLI shows; <see cref="Detail"/>
/// is the full exception text written to the run logs (a dev tool keeps the detail — just not as
/// the headline). <see cref="Remedy"/> is an actionable next step.
/// </summary>
public sealed record RunAbort
{
    /// <summary>One-line, human-readable summary of what went wrong (the console headline).</summary>
    public required string Headline { get; init; }

    /// <summary>An actionable next step for the human (e.g. "run git online", "fix the hook").</summary>
    public required string Remedy { get; init; }

    /// <summary>The full fault text (typically the exception's ToString()) for the run logs.</summary>
    public required string Detail { get; init; }
}
