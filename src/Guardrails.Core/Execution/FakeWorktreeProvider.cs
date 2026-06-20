namespace Guardrails.Core.Execution;

/// <summary>
/// No-op <see cref="IWorktreeProvider"/> for use in tests and non-worktree runs. All
/// operations succeed without touching git: handles carry placeholder path strings, and
/// <see cref="Integrate"/> returns <see cref="IntegrationResult.FastForward"/> immediately.
/// </summary>
public sealed class FakeWorktreeProvider : IWorktreeProvider
{
    public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
        new()
        {
            IntegrationWorktreePath = $"fake/worktrees/{runId}/_integration",
            PlanBranchName = $"guardrails/{planName}",
            OriginalBranch = "main",
            OriginalHeadSha = "0000000000000000000000000000000000000000",
            RunId = runId
        };

    public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct) =>
        new()
        {
            WorktreePath = $"fake/worktrees/{integ.RunId}/{taskId}/attempt-{attempt}",
            SegmentBranchName = $"guardrails/{integ.RunId}/{taskId}/attempt-{attempt}",
            TaskBase = "0000000000000000000000000000000000000000",
            RecordedCommitSha = "0000000000000000000000000000000000000000",
            PlanBranchHead = "0000000000000000000000000000000000000000"
        };

    public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) =>
        new()
        {
            WorktreePath = upstreamSegment.WorktreePath,
            SegmentBranchName = $"guardrails/reused/{taskId}/attempt-{attempt}",
            TaskBase = upstreamSegment.RecordedCommitSha,
            RecordedCommitSha = upstreamSegment.RecordedCommitSha,
            PlanBranchHead = upstreamSegment.PlanBranchHead
        };

    public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt) =>
        new()
        {
            WorktreePath = $"fake/worktrees/fork/{taskId}/attempt-{attempt}",
            SegmentBranchName = $"guardrails/fork/{taskId}/attempt-{attempt}",
            TaskBase = producerRecordedSha,
            RecordedCommitSha = producerRecordedSha,
            PlanBranchHead = producerRecordedSha
        };

    public FanInHandle CreateFanIn(
        WorktreeHandle chosenUpstream,
        IReadOnlyList<WorktreeHandle> others,
        string taskId,
        int attempt,
        CancellationToken ct) =>
        new() { PrivateWorktreePath = $"fake/worktrees/fanin/{taskId}/attempt-{attempt}" };

    public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct) =>
        IntegrationResult.FastForward;

    public void Discard(WorktreeHandle handle) { }

    public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) { }

    public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
        MergeOnSuccessResult.FastForwarded;
}
