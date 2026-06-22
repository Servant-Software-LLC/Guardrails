using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// plan 08 topology-wiring M0: the bookkeeping scaffold. The new <c>RunContext.DirectoryOwner</c>
/// map rides exactly the existing <c>CreateSegment</c> call-sites. These tests pin the M0-invariant
/// facts that survive M1: the map's field shape, and that independent root tasks (which never
/// inherit or fork — they have no producer) each still get their OWN fresh segment directory.
/// (The behavioral cases that M1 deliberately changes — linear-chain reuse, fan-out inherit-one —
/// are asserted in <see cref="TopologyM1ReuseForkTests"/>.)
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
    public async Task IndependentRoots_EachGetTheirOwnFreshSegment_NoReuseNoFork()
    {
        // Three independent root tasks have no producer, so they can never inherit or fork — each
        // gets a fresh CreateSegment off the plan tip and owns its own distinct directory. This is
        // the part of the fresh-per-task baseline that M1 preserves (the bookkeeping 1:1 floor).
        PlanDefinition plan = Plan(
            Task("01-a"),
            Task("02-b"),
            Task("03-c"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();

        RunReport report = await Create(plan, executor, provider, parallelism: 3)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        Assert.Equal(3, provider.CreateCalls.Count);
        Assert.Empty(provider.ReuseCalls);
        Assert.Empty(provider.ForkCalls);
        Assert.Equal(3, executor.AssignedPath.Values.Distinct(StringComparer.Ordinal).Count());
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
