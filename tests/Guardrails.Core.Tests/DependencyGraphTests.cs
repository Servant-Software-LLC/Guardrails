using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

public sealed class DependencyGraphTests
{
    [Fact]
    public void FindCycle_Acyclic_ReturnsNull()
    {
        var graph = new DependencyGraph([
            Task("01-a"),
            Task("02-b", "01-a"),
            Task("03-c", "01-a", "02-b")
        ]);

        Assert.Null(graph.FindCycle());
    }

    [Fact]
    public void FindCycle_ReturnsClosedPath()
    {
        var graph = new DependencyGraph([
            Task("01-a", "03-c"),
            Task("02-b", "01-a"),
            Task("03-c", "02-b")
        ]);

        IReadOnlyList<string>? cycle = graph.FindCycle();

        Assert.NotNull(cycle);
        // Closed: first == last; contains all three participants.
        Assert.Equal(cycle![0], cycle[^1]);
        Assert.Superset(new HashSet<string> { "01-a", "02-b", "03-c" }, cycle.ToHashSet());
        Assert.Equal(4, cycle.Count);
    }

    [Fact]
    public void FindCycle_SelfDependency_IsACycle()
    {
        var graph = new DependencyGraph([Task("01-a", "01-a")]);

        IReadOnlyList<string>? cycle = graph.FindCycle();

        Assert.NotNull(cycle);
        Assert.Equal(["01-a", "01-a"], cycle);
    }

    [Fact]
    public void Waves_DiamondGroupsCorrectly()
    {
        var graph = new DependencyGraph([
            Task("01-root"),
            Task("02-left", "01-root"),
            Task("03-right", "01-root"),
            Task("04-join", "02-left", "03-right")
        ]);

        IReadOnlyList<IReadOnlyList<TaskNode>> waves = graph.Waves();

        Assert.Equal(3, waves.Count);
        Assert.Equal(["01-root"], waves[0].Select(t => t.Id));
        Assert.Equal(["02-left", "03-right"], waves[1].Select(t => t.Id));
        Assert.Equal(["04-join"], waves[2].Select(t => t.Id));
    }

    [Fact]
    public void TransitiveDependents_CoversWholeDownstreamClosure()
    {
        var graph = new DependencyGraph([
            Task("01-root"),
            Task("02-mid", "01-root"),
            Task("03-leaf", "02-mid"),
            Task("04-other")
        ]);

        IReadOnlySet<string> closure = graph.TransitiveDependentsOf("01-root");

        Assert.Equal(new HashSet<string> { "02-mid", "03-leaf" }, closure);
    }
}
