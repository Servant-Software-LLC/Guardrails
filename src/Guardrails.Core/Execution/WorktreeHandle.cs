namespace Guardrails.Core.Execution;

/// <summary>
/// Identifies a task's isolated git worktree segment for plan 08 parallel execution (SSOT §1).
/// Carries the segment path, branch name, the commit the task started from (taskBase), the
/// commit recorded after the task succeeds, and the plan-branch HEAD the segment descends from.
/// Created by <see cref="IWorktreeProvider.CreateSegment"/> and threaded into
/// <see cref="ITaskExecutor.ExecuteAsync"/> so the executor can scope all writes to the segment.
/// </summary>
public sealed class WorktreeHandle
{
    /// <summary>Absolute path to the worktree directory for this task segment.</summary>
    public string WorktreePath { get; init; } = "";

    /// <summary>Name of the git branch this segment writes to (e.g. <c>guardrails/run-abc/01-task/attempt-1</c>).</summary>
    public string SegmentBranchName { get; init; } = "";

    /// <summary>
    /// The commit sha the task must reset to on retry — the tip of the plan branch BEFORE this
    /// task's work, so a reset discards only this task's WIP and never upstream segments' commits.
    /// </summary>
    public string TaskBase { get; init; } = "";

    /// <summary>
    /// The commit sha recorded after the task's action was committed to the segment branch.
    /// Mutable: <see cref="IWorktreeProvider.Integrate"/> captures the segment's commit sha here
    /// (C2) so a downstream fan-out <see cref="IWorktreeProvider.ForkFromTip"/> forks off the
    /// producer's recorded sha rather than a live (possibly inheritor-advanced) segment tip (W-2).
    /// </summary>
    public string RecordedCommitSha { get; set; } = "";

    /// <summary>The plan-branch HEAD sha this segment was forked from.</summary>
    public string PlanBranchHead { get; init; } = "";

    /// <summary>The task id this segment was created for (used for commit trailers in Integrate).</summary>
    public string TaskId { get; init; } = "";

    /// <summary>
    /// The task's <c>TaskDefinitionHash</c> (SSOT §7.2, issue #274 Part A) the Scheduler stamps here
    /// immediately BEFORE <see cref="IWorktreeProvider.Integrate"/>, so the integration commit carries the
    /// <c>Guardrails-Task-Hash:</c> trailer on the FF path (the segment commit that becomes the plan-branch
    /// commit). Threaded on the mutable handle rather than a new <see cref="IWorktreeProvider.Integrate"/>
    /// parameter so the many fake providers implementing that interface method keep compiling. Null when
    /// unavailable — the trailer line is then omitted (backward-compatible).
    /// </summary>
    public string? DefinitionHash { get; set; }
}
