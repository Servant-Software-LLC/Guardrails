using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// RED tests for plan 08 M1: encode the IWorktreeProvider seam, WorktreeHandle /
/// IntegrationHandle shapes, the updated ITaskExecutor.ExecuteAsync signature (now
/// takes a WorktreeHandle), and scheduler-level overlap — all BEFORE any of these
/// types exist. This file MUST fail to compile against pre-M1 code because
/// IWorktreeProvider, WorktreeHandle, IntegrationHandle, IntegrationResult,
/// MergeOnSuccessResult, and the new ExecuteAsync overload do not yet
/// exist. That compile failure IS the "fails on current code" signal.
/// Do NOT implement the seam to make these compile; implement M1 instead.
/// </summary>
public sealed class WorktreeProviderSeamTests
{
    // -------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------

    private sealed class FakeJournal : ISchedulerJournal
    {
        public JournalTaskStatus StatusOf(string taskId) => JournalTaskStatus.Pending;
        public void MarkBlocked(string taskId) { }
    }

    /// <summary>
    /// Minimal IWorktreeProvider for M1 seam tests.
    /// Integrate is a no-op (returns FastForward).
    /// CreateSegment and ReuseSegment return WorktreeHandles with real field values
    /// that the property-shape tests can assert on.
    /// </summary>
    private sealed class FakeWorktreeProvider : IWorktreeProvider
    {
        public List<WorktreeHandle> CreatedSegments { get; } = [];
        public List<WorktreeHandle> ReusedSegments { get; } = [];

        // ROOT CAUSE of the issue-#214 flake (confirmed by an instrumented local repro:
        // 13 runs, this counter read 1/2/3 while every other assertion was stable and the
        // barrier opened every time — i.e. NOT thread-pool starvation). The Scheduler calls
        // IWorktreeProvider.Integrate CONCURRENTLY: the provider.Integrate(...) invocation in
        // Scheduler.OnSettledAsync runs OUTSIDE the scheduler's _gate, so the moment the
        // rendezvous barrier releases all 3 tasks at once, all 3 worker threads enter Integrate
        // together. A plain `IntegrateCallCount++` is a non-atomic read-modify-write, so those
        // concurrent increments lose updates and the counter settles at 1 or 2 — which is what
        // `Assert.Equal(3, provider.IntegrateCallCount)` intermittently saw. The fix is to make
        // the increment atomic; this does NOT weaken the assertion — Integrate genuinely runs
        // exactly 3 times, and the counter now records all 3.
        private int _integrateCallCount;
        public int IntegrateCallCount => Volatile.Read(ref _integrateCallCount);

        public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
            new()
            {
                IntegrationWorktreePath = $"/fake/_integration/{runId}",
                PlanBranchName = $"guardrails/{planName}",
                OriginalBranch = "main",
                OriginalHeadSha = "deadbeef0000",
                RunId = runId
            };

        public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct)
        {
            var h = new WorktreeHandle
            {
                WorktreePath = $"/fake/worktrees/{integ.RunId}/{taskId}/attempt-{attempt}",
                SegmentBranchName = $"guardrails/{integ.RunId}/{taskId}/attempt-{attempt}",
                TaskBase = "aaaaaa00",
                RecordedCommitSha = "bbbbbb00",
                PlanBranchHead = "cccccc00"
            };
            CreatedSegments.Add(h);
            return h;
        }

        public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt)
        {
            var h = new WorktreeHandle
            {
                WorktreePath = upstreamSegment.WorktreePath,
                SegmentBranchName = $"guardrails/reused/{taskId}/attempt-{attempt}",
                // W-2 invariant: taskBase MUST be the upstream's recorded commit sha so
                // a retry-reset discards only this task's WIP, never upstream's commits.
                TaskBase = upstreamSegment.RecordedCommitSha,
                RecordedCommitSha = upstreamSegment.RecordedCommitSha,
                PlanBranchHead = upstreamSegment.PlanBranchHead
            };
            ReusedSegments.Add(h);
            return h;
        }

        public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt) =>
            new()
            {
                WorktreePath = $"/fake/worktrees/fork/{taskId}/attempt-{attempt}",
                SegmentBranchName = $"guardrails/fork/{taskId}/attempt-{attempt}",
                TaskBase = producerRecordedSha,
                RecordedCommitSha = producerRecordedSha,
                PlanBranchHead = producerRecordedSha
            };

        public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct)
        {
            // Atomic — see the IntegrateCallCount field comment: the Scheduler calls this
            // concurrently from its worker threads (off-gate), so a plain ++ would race.
            Interlocked.Increment(ref _integrateCallCount);
            return IntegrationResult.FastForward;
        }

        public void Discard(WorktreeHandle handle) { }

        public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) { }

        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
            MergeOnSuccessResult.FastForwarded;
    }

    /// <summary>
    /// Executor with the new M1 signature (TaskNode + WorktreeHandle + CancellationToken).
    /// Uses a rendezvous barrier: all N tasks must arrive simultaneously before any can
    /// finish, which proves they overlapped. If the scheduler runs serially, the barrier
    /// never opens and the test's deadline fires — a timeout, not a false-green.
    /// </summary>
    private sealed class BarrierExecutor : ITaskExecutor
    {
        private readonly int _expectedCount;
        private readonly TaskCompletionSource _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivedCount;

        public ConcurrentBag<string> AssignedWorktreePaths { get; } = [];

        public BarrierExecutor(int expectedCount) => _expectedCount = expectedCount;

        public async Task<TaskResult> ExecuteAsync(
            TaskNode task, WorktreeHandle worktree, CancellationToken ct)
        {
            Assert.NotNull(worktree);
            AssignedWorktreePaths.Add(worktree.WorktreePath);

            // Signal arrival; when all N arrive, open the rendezvous.
            if (Interlocked.Increment(ref _arrivedCount) == _expectedCount)
                _allArrived.TrySetResult();

            // Block until ALL tasks are simultaneously in flight — this IS the overlap proof.
            await _allArrived.Task.WaitAsync(ct);

            return new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" };
        }
    }

    private static Scheduler CreateScheduler(
        PlanDefinition plan,
        ITaskExecutor executor,
        IWorktreeProvider worktreeProvider,
        int parallelism = 3) =>
        new(plan, executor, new FakeJournal(), worktreeProvider, IRunObserver.Null, parallelism);

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void WorktreeHandle_CarriesRequiredProperties()
    {
        // Encodes §1: WorktreeHandle carries worktree path, segment branch name,
        // taskBase commit, recorded commit sha, and plan-branch HEAD it descends from.
        var handle = new WorktreeHandle
        {
            WorktreePath = "/tmp/wt/01-task/attempt-1",
            SegmentBranchName = "guardrails/run-abc/seg-1/attempt-1",
            TaskBase = "aabb0011",
            RecordedCommitSha = "ccdd0022",
            PlanBranchHead = "eeff0033"
        };

        Assert.Equal("/tmp/wt/01-task/attempt-1", handle.WorktreePath);
        Assert.Equal("guardrails/run-abc/seg-1/attempt-1", handle.SegmentBranchName);
        Assert.Equal("aabb0011", handle.TaskBase);
        Assert.Equal("ccdd0022", handle.RecordedCommitSha);
        Assert.Equal("eeff0033", handle.PlanBranchHead);
    }

    [Fact]
    public void IntegrationHandle_CarriesRequiredProperties()
    {
        // Encodes §1: IntegrationHandle carries integration worktree path, plan-branch
        // name, user's original branch + HEAD sha, and runId.
        var handle = new IntegrationHandle
        {
            IntegrationWorktreePath = "/tmp/wt/_integration",
            PlanBranchName = "guardrails/my-plan",
            OriginalBranch = "main",
            OriginalHeadSha = "1234abcd",
            RunId = "run-xyz"
        };

        Assert.Equal("/tmp/wt/_integration", handle.IntegrationWorktreePath);
        Assert.Equal("guardrails/my-plan", handle.PlanBranchName);
        Assert.Equal("main", handle.OriginalBranch);
        Assert.Equal("1234abcd", handle.OriginalHeadSha);
        Assert.Equal("run-xyz", handle.RunId);
    }

    [Fact]
    public void FakeWorktreeProvider_CreateSegment_ReturnsHandleWithAllRequiredFields()
    {
        var provider = new FakeWorktreeProvider();
        IntegrationHandle integ = provider.CreateIntegration("my-plan", "run-1", CancellationToken.None);

        WorktreeHandle handle = provider.CreateSegment("01-task", attempt: 1, integ, CancellationToken.None);

        Assert.NotEmpty(handle.WorktreePath);
        Assert.NotEmpty(handle.SegmentBranchName);
        Assert.NotEmpty(handle.TaskBase);
        Assert.NotEmpty(handle.RecordedCommitSha);
        Assert.NotEmpty(handle.PlanBranchHead);
        Assert.Single(provider.CreatedSegments);
    }

    [Fact]
    public void FakeWorktreeProvider_ReuseSegment_SetsTaskBaseToUpstreamRecordedSha()
    {
        // Encodes W-2: the reused segment's taskBase MUST be the upstream's RECORDED
        // commit sha (captured at the moment the upstream committed) — never a live
        // rev-parse of the segment branch, which the inherit-one successor may have
        // advanced. A retry-reset to taskBase then discards only this task's WIP,
        // never the upstream's committed work.
        var provider = new FakeWorktreeProvider();
        IntegrationHandle integ = provider.CreateIntegration("my-plan", "run-1", CancellationToken.None);
        WorktreeHandle upstream = provider.CreateSegment("01-task", attempt: 1, integ, CancellationToken.None);

        WorktreeHandle reused = provider.ReuseSegment(upstream, "02-task", attempt: 1);

        Assert.Equal(upstream.RecordedCommitSha, reused.TaskBase);
        Assert.Single(provider.ReusedSegments);
    }

    [Fact]
    public void FakeWorktreeProvider_Integrate_IsNoOpAndCountable()
    {
        var provider = new FakeWorktreeProvider();
        IntegrationHandle integ = provider.CreateIntegration("my-plan", "run-1", CancellationToken.None);
        WorktreeHandle segment = provider.CreateSegment("01-task", attempt: 1, integ, CancellationToken.None);

        IntegrationResult result = provider.Integrate(segment, integ, CancellationToken.None);

        Assert.Equal(IntegrationResult.FastForward, result);
        Assert.Equal(1, provider.IntegrateCallCount);
    }

    [Fact]
    public async Task Scheduler_DrivesThreeIndependentTasks_WithWorktreeHandles_OverlapProvenByBarrier()
    {
        // Three independent tasks at maxParallelism=3 with a rendezvous barrier.
        // The barrier requires all 3 to arrive simultaneously before any finishes.
        // If the scheduler runs tasks serially (regression), the 3rd never starts
        // while the 1st is blocked, the barrier never opens, and the 30-second
        // CancellationToken fires — making the regression a test timeout, not a
        // false-green.
        //
        // On success: all 3 tasks got distinct WorktreeHandles, and Integrate was
        // called once per task (proving the full envelope: task + handle → integrate).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var provider = new FakeWorktreeProvider();
        var executor = new BarrierExecutor(expectedCount: 3);

        PlanDefinition plan = Plan(
            Task("01-a"),
            Task("02-b"),
            Task("03-c"));

        RunReport report = await CreateScheduler(plan, executor, provider, parallelism: 3)
            .RunAsync(plan, cts.Token);

        Assert.True(report.AllSucceeded, "all 3 independent tasks should succeed");

        // Each task received a distinct WorktreeHandle from the provider.
        Assert.Equal(3, executor.AssignedWorktreePaths.Count);
        Assert.Equal(3, executor.AssignedWorktreePaths.Distinct().Count());

        // The provider created one segment worktree per task.
        Assert.Equal(3, provider.CreatedSegments.Count);

        // Integrate was called once per task (the per-task envelope settles).
        Assert.Equal(3, provider.IntegrateCallCount);
    }
}
