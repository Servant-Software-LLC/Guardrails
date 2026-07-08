namespace Guardrails.Core.Execution;

/// <summary>
/// One task's durable resume facts recovered from a <c>Guardrails-Task:</c> trailer on the plan branch
/// (issue #274 Part A, SSOT §7.2): the integration commit that bears it (<see cref="CommitSha"/> — the
/// definition-drift report's git-recovery anchor) and the <c>Guardrails-Task-Hash:</c> definition hash
/// recorded there (<see cref="DefinitionHash"/>, null on a commit predating that trailer line —
/// backward-compatible).
/// </summary>
public sealed record PlanBranchTaskRecord(string CommitSha, string? DefinitionHash);

/// <summary>
/// One wave's durable resume facts recovered from a <c>Guardrails-Wave:</c> marker commit on the plan
/// branch (SSOT §14.5, decision E): the empty marker commit's sha (<see cref="MarkerSha"/> — the
/// wave-scoped-rewind anchor) and the <c>Guardrails-Wave-Hash:</c> definition hash recorded there
/// (<see cref="WaveDefinitionHash"/>, null on a marker predating that line). The wave-level analogue of
/// <see cref="PlanBranchTaskRecord"/>; the backstop that survives <c>run.json</c> loss.
/// </summary>
public sealed record PlanBranchWaveRecord(string MarkerSha, string? WaveDefinitionHash);

/// <summary>
/// Creates and manages git worktrees for parallel plan execution (plan 08 M1 seam).
/// Implementations are responsible for the physical git operations (M2); this interface
/// is the seam between the scheduler and the worktree subsystem.
/// </summary>
public interface IWorktreeProvider
{
    /// <summary>
    /// Create the shared integration worktree and plan branch for a run. Called once at the
    /// start of <see cref="Scheduler.RunAsync"/>; the returned handle is shared across all tasks.
    /// </summary>
    IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct);

    /// <summary>
    /// Create an isolated segment worktree for <paramref name="taskId"/> rooted at the current
    /// plan-branch tip. Called before each task is dispatched to the executor.
    /// </summary>
    WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct);

    /// <summary>
    /// Return a new <see cref="WorktreeHandle"/> that re-uses <paramref name="upstreamSegment"/>'s
    /// worktree path but sets <c>TaskBase</c> to the upstream's <c>RecordedCommitSha</c> (W-2
    /// invariant: a retry-reset discards only this task's WIP, never upstream's commits).
    /// </summary>
    WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt);

    /// <summary>
    /// Fork a new worktree from a specific producer's recorded commit sha — used when a fan-in
    /// task wants to start from a specific upstream rather than the current plan-branch tip.
    /// </summary>
    WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt);

    /// <summary>
    /// Fast-forward (or merge) the plan branch in the integration worktree to include the commits
    /// in <paramref name="segment"/>. Called once per task after the executor reports success.
    /// </summary>
    IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct);

    /// <summary>Remove a segment worktree (e.g. after a permanent failure or cancellation).</summary>
    void Discard(WorktreeHandle handle);

    /// <summary>
    /// Remove any segment worktrees that are not in <paramref name="liveTaskIds"/> — defensive
    /// cleanup to avoid orphaned branches from a prior crashed run.
    /// </summary>
    void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ);

    /// <summary>
    /// After all tasks succeed, fast-forward (or merge) the user's original branch to the tip of
    /// the completed plan branch (plan 08 SSOT §5.3 / mergeOnSuccess). The user-facing merge commit
    /// KEEPS the user's git hooks (issue #149): when a hook rejects it the result is
    /// <see cref="MergeOnSuccessResult.HookRejected"/> and the hook's stderr is exposed via
    /// <see cref="LastMergeOnSuccessDetail"/> for the Scheduler to thread into the report.
    /// </summary>
    MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct);

    /// <summary>
    /// The git hook's stderr captured by the most recent <see cref="MergePlanBranchIntoUserBranch"/>
    /// call when it returned <see cref="MergeOnSuccessResult.HookRejected"/> (issues #149/#150);
    /// null otherwise. Read by the Scheduler immediately after the merge call to populate
    /// <see cref="RunReport.MergeOnSuccessDetail"/>. Default null for fake providers that have no
    /// real git hooks.
    /// </summary>
    string? LastMergeOnSuccessDetail => null;

    /// <summary>
    /// Undo a non-FF merge that was staged in the integration worktree by <see cref="Integrate"/>
    /// but whose re-verify failed (B1 four-effect rollback). Resets the integration worktree to
    /// the pre-merge HEAD. Default is a no-op for in-process fake providers that have no git state.
    /// <see cref="GitWorktreeProvider"/> overrides this with a real <c>git reset --hard</c>.
    /// </summary>
    void RollbackMerge(IntegrationHandle integ, CancellationToken ct) { }

    /// <summary>
    /// Delete this run's stale segment/fork branches (<c>guardrails/&lt;runId&gt;/*</c>) and their
    /// worktrees before the resume reconcile reads trailers, so a crashed prior run's segment refs
    /// can't be mistaken for integrated work (W-1). Default no-op for fake providers.
    /// </summary>
    void PruneStaleRunBranches(string runId, IntegrationHandle integ) { }

    /// <summary>
    /// Resume reconciliation (B1_1/F1): return every task already integrated onto the plan branch (a
    /// <c>Guardrails-Task:</c> trailer reachable from the tip), regardless of which run committed it,
    /// keyed by task id to its <see cref="PlanBranchTaskRecord"/> (the bearing commit sha + the recorded
    /// <c>Guardrails-Task-Hash:</c>). The Scheduler unions the KEYS with the journal's Succeeded set
    /// during the resume pre-pass so a task integrated before its journal write was lost is not re-run,
    /// AND uses the record to compare the recorded definition hash + locate the old commit for the
    /// definition-drift report (§7.2, #274 Part A). Default is the empty map for fake providers that keep
    /// no durable branch.
    /// </summary>
    IReadOnlyDictionary<string, PlanBranchTaskRecord> ReconcileFromPlanBranch(IntegrationHandle integ) =>
        new Dictionary<string, PlanBranchTaskRecord>(StringComparer.Ordinal);

    /// <summary>
    /// Recover a file's text as it was at <paramref name="commitSha"/> for the definition-drift report's
    /// Tier-2 per-file breakdown (issue #274 Part A, SSOT §7.2), or null when the bytes are not
    /// recoverable — the path was not tracked at that commit, the commit is unresolvable, or the provider
    /// keeps no git history (fake providers, serial mode). Read-only and best-effort: Tier 1 (the
    /// aggregate old→new hash) never depends on it. Default null.
    /// </summary>
    string? ReadFileAtCommit(string commitSha, string absolutePath) => null;

    /// <summary>
    /// The <c>/</c>-normalized path of <paramref name="absolutePath"/> relative to the workspace repo
    /// root, for the definition-drift report's reference <c>git diff</c> command (issue #274 Part A).
    /// Null when the provider has no repo root (fake providers) or the path escapes it.
    /// </summary>
    string? RepoRelativePath(string absolutePath) => null;

    /// <summary>
    /// The files present under <paramref name="absoluteDir"/> at <paramref name="commitSha"/> (issue #274
    /// Part A), as ABSOLUTE paths — so the drift report's per-file breakdown can name a file that was
    /// REMOVED (present then, absent now) which the current on-disk enumeration no longer yields. Empty
    /// when the directory was not tracked at that commit, the commit is unresolvable, or the provider
    /// keeps no git history. Read-only and best-effort. Default empty.
    /// </summary>
    IReadOnlyList<string> ListFilesAtCommit(string commitSha, string absoluteDir) => [];

    /// <summary>
    /// Commit a non-FF union that <see cref="Integrate"/> staged with <c>merge --no-commit</c>,
    /// stamping the <c>Guardrails-Task:</c>/<c>Guardrails-Run:</c> trailer onto the merge commit
    /// (B2) so the settled task leaves a first-parent trailer on the plan branch like the FF path.
    /// Called by the Scheduler only after the union re-verify passes. Returns the new plan-branch
    /// HEAD sha. When <paramref name="definitionHash"/> is non-null it is written as a third
    /// <c>Guardrails-Task-Hash:</c> trailer line on the merge commit (issue #274 Part A, §7.2), matching
    /// the FF path; omitted when null (backward-compatible). Default is a no-op (empty) for fake
    /// providers that never produce a non-FF merge.
    /// </summary>
    string CommitStagedMerge(
        IntegrationHandle integ, string taskId, CancellationToken ct, string? definitionHash = null) => "";

    /// <summary>
    /// Retry-salvage pruning (issue #195, deliverable 6): delete every preserved salvage ref for
    /// <paramref name="taskId"/> (<c>refs/guardrails/&lt;taskId&gt;/attempt-*</c>) — called once a task
    /// settles <c>succeeded</c>, so a green task's salvage refs (its own rolled-back partial attempts)
    /// do not linger forever in a long-lived repo. Default no-op for fake providers that keep no
    /// durable refs.
    /// </summary>
    void PruneSalvageRefs(string taskId) { }

    /// <summary>
    /// True when this provider maintains the durable <c>Guardrails-Task:</c> trailer record on a plan
    /// branch (issue #274 Part C). It gates the resume reconciliation invariant "a journal-<c>Succeeded</c>
    /// task whose integration trailer is ABSENT from the current plan-branch history MUST re-run" — sound
    /// ONLY where the branch is the authoritative integration record. Default <c>false</c> (serial mode /
    /// fake providers keep no trailers, so the journal alone is authoritative there);
    /// <see cref="GitWorktreeProvider"/> overrides to <c>true</c>.
    /// </summary>
    bool TracksPlanBranchTrailers => false;

    /// <summary>
    /// The current plan-branch tip sha (issue #274 Part C) — the compare-and-swap read taken immediately
    /// before a destructive rewind so a concurrent same-plan session (or an operator editing between a
    /// prompt and its confirmation) that advanced the branch is DETECTED and the rewind refused. Default
    /// empty string for fake/serial providers.
    /// </summary>
    string CurrentPlanBranchTip(IntegrationHandle integ) => "";

    /// <summary>
    /// Part C safe-auto-resolve (issue #274, SSOT §7.2): evaluate whether the drifted set
    /// <paramref name="safeSet"/> forms a provably-safe trailing suffix of the plan branch that a
    /// destructive <c>git reset --hard</c> can rewind past — via the pure
    /// <see cref="SafeSuffixEvaluator"/> over the plan branch's <c>--first-parent</c> trailer history +
    /// each merge's non-first-parent lineage (the merge-tip caveat). READ-ONLY. Default
    /// <see cref="SafeSuffixDecision.Nothing"/> for fake/serial providers with no plan branch to rewind
    /// (the caller then falls back to a sound journal-only reset).
    /// </summary>
    SafeSuffixDecision EvaluateSafeSuffix(IntegrationHandle integ, IReadOnlySet<string> safeSet) =>
        SafeSuffixDecision.Nothing();

    /// <summary>
    /// Part C safe-auto-resolve (issue #274, SSOT §7.2): DESTRUCTIVELY rewind the plan branch to
    /// <paramref name="resetTarget"/> (the <see cref="SafeSuffixDecision.ResetTarget"/> a prior
    /// <see cref="EvaluateSafeSuffix"/> proved safe), physically removing the drifted suffix so the
    /// re-run forks from a base that no longer carries the stale commits. The discarded commits stay
    /// recoverable via the branch reflog. Called ONLY at the pre-DAG gate, before any segment worktree is
    /// forked off the soon-to-be-rewound tip. Default no-op for fake providers.
    /// </summary>
    void RewindPlanBranchTo(IntegrationHandle integ, string resetTarget) { }

    // --- Multi-wave marker commits (SSOT §14.5, decision E, #254 M2b) ---------------------

    /// <summary>
    /// Write the durable <c>Guardrails-Wave:</c> marker commit (SSOT §14.5, decision E) on the plan
    /// branch when a wave completes: an EMPTY commit in the integration worktree carrying
    /// <c>Guardrails-Wave: &lt;waveDir&gt;</c> / <c>Guardrails-Wave-Hash: &lt;waveHash&gt;</c> /
    /// <c>Guardrails-Run: &lt;runId&gt;</c> — the wave-level analogue of the task integration commit's
    /// trailer triple, and the Part C wave-scoped-rewind boundary. Returns the marker commit sha. Default
    /// empty string for fake/serial providers that keep no plan branch.
    /// </summary>
    string CommitWaveMarker(IntegrationHandle integ, string waveDir, string waveHash, CancellationToken ct) => "";

    /// <summary>
    /// Resume reconciliation for waves (SSOT §14.5/§14.6): every wave with a <c>Guardrails-Wave:</c>
    /// marker commit reachable from the plan-branch tip, keyed by wave dir → its
    /// <see cref="PlanBranchWaveRecord"/> (marker sha + recorded <c>Guardrails-Wave-Hash:</c>). The
    /// durable cross-run wave-completion record — the backstop when <c>run.json</c> is lost, and the
    /// source of the predecessor-wave rewind anchor. Default empty map for fake providers.
    /// </summary>
    IReadOnlyDictionary<string, PlanBranchWaveRecord> ReconcileWavesFromPlanBranch(IntegrationHandle integ) =>
        new Dictionary<string, PlanBranchWaveRecord>(StringComparer.Ordinal);

    /// <summary>
    /// The plan branch's base commit (SSOT §14.8) — the fork point off the user's branch, i.e. the tip
    /// before ANY wave integrated. It is the wave-scoped-rewind target when rewinding the FIRST wave (which
    /// has no predecessor marker to rewind to). Computed as <c>git merge-base &lt;planBranch&gt;
    /// &lt;originalBranch&gt;</c>. Default empty string for fake/serial providers.
    /// </summary>
    string PlanBranchBase(IntegrationHandle integ) => "";
}
