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
    /// Create a private fan-in worktree that merges <paramref name="chosenUpstream"/> with
    /// <paramref name="others"/> before a fan-in task runs (plan 08 M5).
    /// </summary>
    FanInHandle CreateFanIn(
        WorktreeHandle chosenUpstream,
        IReadOnlyList<WorktreeHandle> others,
        string taskId,
        int attempt,
        CancellationToken ct);

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
    /// the completed plan branch (plan 08 SSOT §5.3 / mergeOnSuccess).
    /// </summary>
    MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct);
}
