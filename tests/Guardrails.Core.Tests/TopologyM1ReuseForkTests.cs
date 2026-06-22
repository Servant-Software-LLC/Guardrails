using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// plan 08 topology-wiring M1 (§A/§B): unit tests that assert WHICH topology call the scheduler
/// made — reuse vs fork vs fresh-segment — using the no-git <see cref="RecordingWorktreeProvider"/>.
/// Covers the design's T-1…T-5: linear-chain reuse, fan-out inherit-one/fork-rest, the
/// longest-downstream-chain (ordinal-id tiebreak) inheritor predicate, fan-in never reusing, and the
/// diamond mix.
/// </summary>
public sealed class TopologyM1ReuseForkTests
{
    private sealed class FakeJournal : ISchedulerJournal
    {
        public JournalTaskStatus StatusOf(string taskId) => JournalTaskStatus.Pending;
        public void MarkBlocked(string taskId) { }
    }

    private static Scheduler Create(PlanDefinition plan, ITaskExecutor executor, IWorktreeProvider provider, int parallelism) =>
        new(plan, executor, new FakeJournal(), provider, IRunObserver.Null, parallelism);

    // ── T-1 ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T1_LinearChain_EachHopReusesTheSameDirectory_OneReusePerHop_NoForks()
    {
        // A→B→C: B reuses A's directory, C reuses B's. One ReuseSegment per hop; the only
        // CreateSegment is the root's; zero forks (each producer has exactly one dependent).
        PlanDefinition plan = Plan(
            Task("01-a"),
            Task("02-b", "01-a"),
            Task("03-c", "02-b"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();

        RunReport report = await Create(plan, executor, provider, parallelism: 1)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);

        // Exactly one fresh segment (the root); two reuses (B inherits A, C inherits B); no forks.
        Assert.Single(provider.CreateCalls);
        Assert.Equal("01-a", provider.CreateCalls.Single().TaskId);
        Assert.Equal(2, provider.ReuseCalls.Count);
        Assert.Empty(provider.ForkCalls);

        // The disk lever: all three tasks ran in the SAME physical directory.
        Assert.Single(
            new[] { executor.AssignedPath["01-a"], executor.AssignedPath["02-b"], executor.AssignedPath["03-c"] }
                .Distinct(StringComparer.Ordinal));
    }

    // ── T-2 ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T2_FanOut_AllSingleProducer_ExactlyOneInherits_TheRestFork()
    {
        // P → {D1, D2, D3}, all single-producer leaves of equal (zero) downstream chain length.
        // Exactly one inherits P's directory (lowest ordinal id on the tie); the other two fork.
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

        // One fresh segment (the producer); exactly one reuse; exactly two forks.
        Assert.Single(provider.CreateCalls);
        Assert.Equal("01-p", provider.CreateCalls.Single().TaskId);
        Assert.Single(provider.ReuseCalls);
        Assert.Equal(2, provider.ForkCalls.Count);

        // The inheritor is the lowest-ordinal-id dependent (all chains equal length).
        Assert.Equal("02-d1", provider.ReuseCalls.Single().TaskId);

        // The two forks are the other two dependents, and each forked off the producer's recorded sha.
        Assert.Equal(
            new HashSet<string> { "03-d2", "04-d3" },
            provider.ForkCalls.Select(f => f.TaskId).ToHashSet(StringComparer.Ordinal));
        Assert.All(provider.ForkCalls,
            f => Assert.Equal(RecordingWorktreeProvider.RecordedShaFor("01-p"), f.ProducerRecordedSha));
    }

    // ── T-3 ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T3_InheritorTiebreak_LongerDownstreamChainWins()
    {
        // P → {D-short, D-long}; D-long has a downstream successor, D-short does not. The inheritor
        // is D-long (longest TransitiveDependentsOf), even though its ordinal id is higher.
        PlanDefinition plan = Plan(
            Task("01-p"),
            Task("02-short", "01-p"),
            Task("03-long", "01-p"),
            Task("04-tail", "03-long"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();

        RunReport report = await Create(plan, executor, provider, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);

        // The longer-chain dependent (03-long) inherits P's directory despite the higher ordinal id
        // — it is the FIRST reuse (the fan-out inherit-one decision at P's settle).
        Assert.Equal("03-long", provider.ReuseCalls.First().TaskId);
        // 02-short forks (the other single-producer dependent of P).
        Assert.Equal("02-short", Assert.Single(provider.ForkCalls).TaskId);
        // 04-tail then inherits 03-long's directory (single-producer hop), so it reuses too.
        Assert.Contains(provider.ReuseCalls, r => r.TaskId == "04-tail");
    }

    [Fact]
    public async Task T3b_InheritorTiebreak_EqualChainLength_LowestOrdinalIdWins()
    {
        // Two equal-length-chain dependents: the lowest ordinal id inherits.
        PlanDefinition plan = Plan(
            Task("01-p"),
            Task("03-z", "01-p"),
            Task("02-a", "01-p"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();

        RunReport report = await Create(plan, executor, provider, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        Assert.Equal("02-a", provider.ReuseCalls.Single().TaskId);
        Assert.Equal("03-z", Assert.Single(provider.ForkCalls).TaskId);
    }

    // ── T-4 ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T4_MultiProducerFanInDependent_IsNeverInheritor_GetsFreshSegment()
    {
        // P1, P2 → F (F depends on both). F is a fan-in: it must get a fresh CreateSegment off the
        // plan tip (which already contains both producers' work), never a reuse/fork.
        PlanDefinition plan = Plan(
            Task("01-p1"),
            Task("02-p2"),
            Task("03-fanin", "01-p1", "02-p2"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();

        RunReport report = await Create(plan, executor, provider, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);

        // The fan-in task got a fresh segment, not a reuse or fork.
        Assert.Contains(provider.CreateCalls, c => c.TaskId == "03-fanin");
        Assert.DoesNotContain(provider.ReuseCalls, r => r.TaskId == "03-fanin");
        Assert.DoesNotContain(provider.ForkCalls, f => f.TaskId == "03-fanin");
        // Two producers (roots) + the fan-in = three fresh segments; no reuse/fork anywhere
        // (each producer had exactly one — multi-producer — dependent, which is the fan-in).
        Assert.Equal(3, provider.CreateCalls.Count);
        Assert.Empty(provider.ReuseCalls);
        Assert.Empty(provider.ForkCalls);
    }

    // ── T-5 ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T5_Diamond_SingleProducerLegsInheritOrFork_FanInJoinGetsFreshSegment()
    {
        // A → {B, C} → D. B and C are single-producer dependents of A (one reuses A, one forks);
        // D is a multi-producer fan-in (fresh segment off the plan tip).
        PlanDefinition plan = Plan(
            Task("01-a"),
            Task("02-b", "01-a"),
            Task("03-c", "01-a"),
            Task("04-d", "02-b", "03-c"));
        var provider = new RecordingWorktreeProvider();
        var executor = new RecordingExecutor();

        RunReport report = await Create(plan, executor, provider, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);

        // A's two single-producer dependents: exactly one reuse, exactly one fork.
        Assert.Single(provider.ReuseCalls);
        Assert.Single(provider.ForkCalls);
        Assert.Equal(
            new HashSet<string> { "02-b", "03-c" },
            new[] { provider.ReuseCalls.Single().TaskId, provider.ForkCalls.Single().TaskId }
                .ToHashSet(StringComparer.Ordinal));

        // The diamond join D is a fan-in: fresh segment, never reused/forked.
        Assert.Contains(provider.CreateCalls, c => c.TaskId == "04-d");
        Assert.DoesNotContain(provider.ReuseCalls, r => r.TaskId == "04-d");
        Assert.DoesNotContain(provider.ForkCalls, f => f.TaskId == "04-d");

        // Fresh segments: the root A and the fan-in D.
        Assert.Equal(
            new HashSet<string> { "01-a", "04-d" },
            provider.CreateCalls.Select(c => c.TaskId).ToHashSet(StringComparer.Ordinal));
    }
}
