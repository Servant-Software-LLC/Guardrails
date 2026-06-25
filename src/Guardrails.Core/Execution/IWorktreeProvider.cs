namespace Guardrails.Core.Execution;

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
    /// Resume reconciliation (B1_1/F1): return every task id already integrated onto the plan
    /// branch (a <c>Guardrails-Task:</c> trailer reachable from the tip), regardless of which run
    /// committed it. The Scheduler unions this with the journal's Succeeded set during the resume
    /// pre-pass so a task integrated before its journal write was lost is not re-run. Default is the
    /// empty set for fake providers that keep no durable branch.
    /// </summary>
    IReadOnlySet<string> ReconcileFromPlanBranch(IntegrationHandle integ) =>
        new HashSet<string>();

    /// <summary>
    /// Commit a non-FF union that <see cref="Integrate"/> staged with <c>merge --no-commit</c>,
    /// stamping the <c>Guardrails-Task:</c>/<c>Guardrails-Run:</c> trailer onto the merge commit
    /// (B2) so the settled task leaves a first-parent trailer on the plan branch like the FF path.
    /// Called by the Scheduler only after the union re-verify passes. Returns the new plan-branch
    /// HEAD sha. Default is a no-op (empty) for fake providers that never produce a non-FF merge.
    /// </summary>
    string CommitStagedMerge(IntegrationHandle integ, string taskId, CancellationToken ct) => "";
}
