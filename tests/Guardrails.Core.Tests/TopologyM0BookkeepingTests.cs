using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// plan 08 topology-wiring M0: the bookkeeping scaffold ships with ZERO behavior change. The
/// scheduler still creates one fresh segment per task — no reuse, no fork — and the new
/// <c>RunContext.DirectoryOwner</c> map rides exactly the existing <c>CreateSegment</c> call-sites
/// (initial-ready set and lazy-dependent set). These tests pin the fresh-per-task baseline that M1
/// must preserve everywhere it does NOT reuse: every task owns its own distinct directory.
///
/// The map itself is private scheduler state; its correctness is proven through its observable
/// consequence — exactly one <see cref="IWorktreeProvider.CreateSegment"/> per task, all distinct
/// paths, and never a <see cref="IWorktreeProvider.ReuseSegment"/>/<see cref="IWorktreeProvider.ForkFromTip"/>
/// at the M0 wiring level.
/// </summary>
public sealed class TopologyM0BookkeepingTests
{
    private sealed class FakeJournal : ISchedulerJournal
    {
        public JournalTaskStatus StatusOf(string taskId) => JournalTaskStatus.Pending;
        public void MarkBlocked(string taskId) { }
    }

    private static Scheduler Create(PlanDefinition plan, ITaskExecutor executor, IWorktreeProvider provider, int parallelism = 1) =>
        new(plan, executor, new FakeJournal(), provider, IRunObserver.Null, parallelism);

    [Fact]
    public async Task LinearChain_FreshPerTaskBaseline_OneCreateSegmentPerTask_NoReuseNoFork()
    {
        // M0: a linear chain A→B→C still gets THREE fresh segments (the pre-topology baseline).
        // Reuse is M1; at M0 the bookkeeping must not change which topology call is made.
        PlanDefinition plan = Plan(
            Task("01-a"),
            Task("02-b", "01-a"),
            Task("03-c", "02-b"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();

        RunReport report = await Create(plan, executor, provider, parallelism: 1)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);

        // One fresh segment per task — the fresh-per-task baseline DirectoryOwner mirrors.
        Assert.Equal(3, provider.CreateCalls.Count);
        Assert.Equal(
            new HashSet<string> { "01-a", "02-b", "03-c" },
            provider.CreateCalls.Select(c => c.TaskId).ToHashSet(StringComparer.Ordinal));

        // No reuse, no fork at M0.
        Assert.Empty(provider.ReuseCalls);
        Assert.Empty(provider.ForkCalls);

        // Every task ran in its OWN distinct directory (the bookkeeping baseline owns 1:1).
        Assert.Equal(3, executor.AssignedPath.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task FanOut_FreshPerTaskBaseline_EachDependentGetsItsOwnDirectory()
    {
        // Producer P with three single-producer dependents. At M0 each dependent gets a fresh
        // segment off the plan tip — no inherit-one, no fork-the-rest yet.
        PlanDefinition plan = Plan(
            Task("01-p"),
            Task("02-d1", "01-p"),
            Task("03-d2", "01-p"),
            Task("04-d3", "01-p"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();

        RunReport report = await Create(plan, executor, provider, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);

        // Four fresh segments — one per task, no reuse/fork.
        Assert.Equal(4, provider.CreateCalls.Count);
        Assert.Empty(provider.ReuseCalls);
        Assert.Empty(provider.ForkCalls);

        // All four directories are distinct (fresh-per-task).
        Assert.Equal(4, executor.AssignedPath.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RunContext_HasDirectoryOwnerMap_StringToString()
    {
        // The M0 scaffold field exists with the documented shape: worktree path → owning task id.
        Type? runContext = typeof(Scheduler).GetNestedType(
            "RunContext", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(runContext);

        System.Reflection.PropertyInfo? owner = runContext!.GetProperty(
            "DirectoryOwner",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.NotNull(owner);
        Assert.Equal(typeof(Dictionary<string, string>), owner!.PropertyType);
    }
}
