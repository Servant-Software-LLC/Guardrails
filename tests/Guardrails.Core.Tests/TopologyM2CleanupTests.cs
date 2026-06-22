using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// plan 08 topology-wiring M2 (§C/§D) unit tests: the outcome-scoped end-of-run cleanup sweep and
/// its best-effort (swallow-and-log) robustness, using the no-git <see cref="RecordingWorktreeProvider"/>.
/// The #126 regression and cancellation behavior are proven against real git in the integration suite
/// (T-10/T-11).
/// </summary>
public sealed class TopologyM2CleanupTests
{
    private sealed class FakeJournal : ISchedulerJournal
    {
        public ConcurrentBag<string> Blocked { get; } = [];
        public JournalTaskStatus StatusOf(string taskId) => JournalTaskStatus.Pending;
        public void MarkBlocked(string taskId) => Blocked.Add(taskId);
    }

    /// <summary>Records an observed <see cref="IRunObserver.CleanupFailed"/> so robustness tests can assert it fired.</summary>
    private sealed class CleanupSpyObserver : IRunObserver
    {
        public ConcurrentBag<string> CleanupFailures { get; } = [];
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void CleanupFailed(string owner, Exception error) => CleanupFailures.Add(owner);
    }

    /// <summary>
    /// A minimal no-git provider whose cleanup operations throw — to prove the scheduler swallows
    /// them. Fresh segments per task; FF integration; reuse/fork return placeholder handles.
    /// </summary>
    private sealed class ThrowingCleanupProvider : IWorktreeProvider
    {
        public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
            new()
            {
                IntegrationWorktreePath = $"integ://{runId}/_integration",
                PlanBranchName = $"guardrails/{planName}",
                OriginalBranch = "main",
                OriginalHeadSha = "0",
                RunId = runId
            };

        public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct) =>
            new() { WorktreePath = $"seg://{taskId}", SegmentBranchName = $"seg/{taskId}", TaskId = taskId };

        public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) =>
            new()
            {
                WorktreePath = upstreamSegment.WorktreePath,
                SegmentBranchName = upstreamSegment.SegmentBranchName,
                TaskBase = upstreamSegment.RecordedCommitSha,
                RecordedCommitSha = upstreamSegment.RecordedCommitSha,
                TaskId = taskId
            };

        public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt) =>
            new() { WorktreePath = $"fork://{taskId}", SegmentBranchName = $"fork/{taskId}", TaskId = taskId };

        public FanInHandle CreateFanIn(
            WorktreeHandle chosenUpstream, IReadOnlyList<WorktreeHandle> others,
            string taskId, int attempt, CancellationToken ct) =>
            new() { PrivateWorktreePath = $"fanin://{taskId}" };

        public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct) =>
            IntegrationResult.FastForward;

        public void Discard(WorktreeHandle handle) =>
            throw new InvalidOperationException("git worktree remove --force exited 128 (test)");

        public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) =>
            throw new InvalidOperationException("git worktree prune exited 128 (test)");

        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
            MergeOnSuccessResult.FastForwarded;
    }

    private static Scheduler Create(
        PlanDefinition plan, ITaskExecutor executor, IWorktreeProvider provider,
        FakeJournal journal, IRunObserver observer, int parallelism) =>
        new(plan, executor, journal, provider, observer, parallelism);

    // ── T-9 (failed producer's directory survives for fix/resume; no held-dir Discard, no double-free) ──
    [Fact]
    public async Task T9_FailedProducer_SegmentSurvivesSweep_BlockedDependentsNeverInheritOrFork_NoDoubleFree()
    {
        // A → B → C, A fails (needs-human, 0 retries). Open-risk #4 / §3.2: the failed task's segment
        // must SURVIVE the end-of-run sweep (it is the human's / resume's inspection surface), so it
        // is NOT Discarded. B and C are blocked and never had a worktree created — there is no held
        // directory to free, and nothing is Discarded twice.
        PlanDefinition plan = Plan(
            Task("01-a"),
            Task("02-b", "01-a"),
            Task("03-c", "02-b"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();
        executor.FailTask("01-a");
        var journal = new FakeJournal();

        RunReport report = await Create(plan, executor, provider, journal, IRunObserver.Null, parallelism: 1)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded);

        // The failed producer's segment was NOT Discarded — only green-owned dirs are swept.
        string producerPath = provider.CreateCalls.Single(c => c.TaskId == "01-a").Path;
        Assert.DoesNotContain(producerPath, provider.DiscardedPaths);

        // The blocked dependents never created an inheritor/fork (no segment exists to free).
        Assert.Empty(provider.ReuseCalls);
        Assert.Empty(provider.ForkCalls);
        Assert.DoesNotContain("02-b", provider.CreateCalls.Select(c => c.TaskId));
        Assert.DoesNotContain("03-c", provider.CreateCalls.Select(c => c.TaskId));

        // No path is Discarded more than once (no double-free), and no held directory was freed.
        Assert.All(provider.DiscardedPaths.GroupBy(p => p), g => Assert.Single(g));
    }

    // ── T-9b (a GREEN sibling's dir IS swept while the FAILED task's dir survives — same run) ────
    [Fact]
    public async Task T9b_MixedRun_GreenSiblingSwept_FailedTaskDirSurvives()
    {
        // Two independent roots: 01-ok succeeds, 02-bad fails. The green one's directory is swept;
        // the failed one's survives. This pins that the sweep is outcome-scoped, not blanket.
        PlanDefinition plan = Plan(
            Task("01-ok"),
            Task("02-bad"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();
        executor.FailTask("02-bad");

        RunReport report = await Create(plan, executor, provider, new FakeJournal(), IRunObserver.Null, parallelism: 2)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded);

        string okPath = provider.CreateCalls.Single(c => c.TaskId == "01-ok").Path;
        string badPath = provider.CreateCalls.Single(c => c.TaskId == "02-bad").Path;
        Assert.Contains(okPath, provider.DiscardedPaths);       // green → swept
        Assert.DoesNotContain(badPath, provider.DiscardedPaths); // failed → survives
    }

    // ── T-12 (cleanup failures swallowed — a green run stays green) ──────────────────────────────
    [Fact]
    public async Task T12_CleanupFailureIsSwallowed_GreenRunStaysGreen_AndIsObserved()
    {
        // The end-of-run sweep's Discard + PruneOrphans both throw; the run must remain AllSucceeded
        // and the failures must be reported through the observer (logged), not propagated.
        PlanDefinition plan = Plan(
            Task("01-a"),
            Task("02-b", "01-a"));
        var provider = new ThrowingCleanupProvider();
        var executor = new RecordingExecutor();
        var observer = new CleanupSpyObserver();

        RunReport report = await Create(plan, executor, provider, new FakeJournal(), observer, parallelism: 1)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, "a cleanup failure must never flip a green run off-green");
        Assert.NotEmpty(observer.CleanupFailures);                  // logged, not silent
        Assert.Contains("(prune-orphans)", observer.CleanupFailures); // prune failure also swallowed
    }
}
