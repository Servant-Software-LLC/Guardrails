using System.Collections.Concurrent;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// A no-git <see cref="IWorktreeProvider"/> for the plan-08 topology-wiring unit tests
/// (M0/M1/M2). It records WHICH topology call the scheduler made for each task and returns
/// handles with distinct, recognizable placeholder paths per method so a test can assert the
/// scheduler chose reuse vs fork vs fresh-segment WITHOUT touching git.
///
/// Path conventions (the test seam):
/// <list type="bullet">
///   <item><c>seg://{runId}/{taskId}</c> — a fresh <see cref="CreateSegment"/> directory.</item>
///   <item>(reuse) — <see cref="ReuseSegment"/> returns the UPSTREAM's path unchanged, so an
///     inheritor shares the producer's <c>seg://…</c> path (the disk lever, observable as a
///     repeated path).</item>
///   <item><c>fork://{taskId}</c> — a <see cref="ForkFromTip"/> directory (a distinct tree off
///     the producer's recorded sha).</item>
/// </list>
/// Every call is recorded so tests can count them (the metamorphic add-count proxy) and inspect
/// the arguments (e.g. the producer recorded sha a fork rooted off — the W-2 assertion).
/// </summary>
public sealed class RecordingWorktreeProvider : IWorktreeProvider
{
    public sealed record CreateCall(string TaskId, int Attempt, string Path);
    public sealed record ReuseCall(string UpstreamPath, string TaskId, int Attempt, string ResultPath, string TaskBase);
    public sealed record ForkCall(string ProducerRecordedSha, string TaskId, int Attempt, string Path);

    public ConcurrentQueue<CreateCall> CreateCalls { get; } = [];
    public ConcurrentQueue<ReuseCall> ReuseCalls { get; } = [];
    public ConcurrentQueue<ForkCall> ForkCalls { get; } = [];
    public ConcurrentQueue<string> DiscardedPaths { get; } = [];
    public int PruneOrphansCallCount;
    public ConcurrentBag<string> IntegratedTaskIds { get; } = [];

    /// <summary>
    /// Per-task recorded commit sha handed back by <see cref="Integrate"/>. Each settled task gets
    /// a unique sha so a fork's <c>producerRecordedSha</c> can be matched to a specific producer
    /// (the W-2 assertion). Default is a deterministic function of the task id.
    /// </summary>
    public static string RecordedShaFor(string taskId) => $"sha-{taskId}";

    public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
        new()
        {
            IntegrationWorktreePath = $"integ://{runId}/_integration",
            PlanBranchName = $"guardrails/{planName}",
            OriginalBranch = "main",
            OriginalHeadSha = "0000000000000000000000000000000000000000",
            RunId = runId
        };

    public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct)
    {
        string path = $"seg://{integ.RunId}/{taskId}/attempt-{attempt}";
        CreateCalls.Enqueue(new CreateCall(taskId, attempt, path));
        return new WorktreeHandle
        {
            WorktreePath = path,
            SegmentBranchName = $"guardrails/{integ.RunId}/{taskId}/attempt-{attempt}",
            TaskBase = "base-" + taskId,
            RecordedCommitSha = "",
            PlanBranchHead = "planhead",
            TaskId = taskId
        };
    }

    public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt)
    {
        var handle = new WorktreeHandle
        {
            // The disk lever: same physical directory as the producer.
            WorktreePath = upstreamSegment.WorktreePath,
            SegmentBranchName = upstreamSegment.SegmentBranchName,
            TaskBase = upstreamSegment.RecordedCommitSha,
            RecordedCommitSha = upstreamSegment.RecordedCommitSha,
            PlanBranchHead = upstreamSegment.PlanBranchHead,
            TaskId = taskId
        };
        ReuseCalls.Enqueue(new ReuseCall(
            upstreamSegment.WorktreePath, taskId, attempt, handle.WorktreePath, handle.TaskBase));
        return handle;
    }

    public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt)
    {
        string path = $"fork://{taskId}/attempt-{attempt}";
        ForkCalls.Enqueue(new ForkCall(producerRecordedSha, taskId, attempt, path));
        return new WorktreeHandle
        {
            WorktreePath = path,
            SegmentBranchName = $"guardrails/fork/{taskId}/attempt-{attempt}",
            TaskBase = producerRecordedSha,
            RecordedCommitSha = producerRecordedSha,
            PlanBranchHead = producerRecordedSha,
            TaskId = taskId
        };
    }

    public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct)
    {
        string taskId = string.IsNullOrEmpty(segment.TaskId) ? segment.SegmentBranchName : segment.TaskId;
        IntegratedTaskIds.Add(taskId);
        // Stamp a deterministic per-task recorded sha so a downstream ForkFromTip's
        // producerRecordedSha can be matched back to this producer (W-2 unit assertion).
        segment.RecordedCommitSha = RecordedShaFor(taskId);
        return IntegrationResult.FastForward;
    }

    public void Discard(WorktreeHandle handle) => DiscardedPaths.Enqueue(handle.WorktreePath);

    public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) =>
        Interlocked.Increment(ref PruneOrphansCallCount);

    public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
        MergeOnSuccessResult.FastForwarded;
}
