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
    public void Tiers_DiamondGroupsCorrectly()
    {
        var graph = new DependencyGraph([
            Task("01-root"),
            Task("02-left", "01-root"),
            Task("03-right", "01-root"),
            Task("04-join", "02-left", "03-right")
        ]);

        IReadOnlyList<IReadOnlyList<TaskNode>> tiers = graph.Tiers();

        Assert.Equal(3, tiers.Count);
        Assert.Equal(["01-root"], tiers[0].Select(t => t.Id));
        Assert.Equal(["02-left", "03-right"], tiers[1].Select(t => t.Id));
        Assert.Equal(["04-join"], tiers[2].Select(t => t.Id));
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

    [Fact]
    public void TransitiveDependencies_CoversWholeAncestorClosure()
    {
        // Mirror of the real task-08 graph: 08 → {01, 04, 05}; 04 → 02; 05 → 02.
        // Closure must be {01, 02, 04, 05} — excludes 03 (unrelated) and dependents.
        var graph = new DependencyGraph([
            Task("01-research"),
            Task("02-restructure"),
            Task("03-decide"),
            Task("04-shell", "02-restructure"),
            Task("05-abstraction", "02-restructure"),
            Task("08-onprem", "04-shell", "05-abstraction", "01-research")
        ]);

        IReadOnlySet<string> ancestors = graph.TransitiveDependenciesOf("08-onprem");

        Assert.Equal(
            new HashSet<string> { "01-research", "02-restructure", "04-shell", "05-abstraction" },
            ancestors);
        Assert.DoesNotContain("03-decide", ancestors);
    }

    [Fact]
    public void TransitiveDependencies_NoDeps_IsEmpty()
    {
        var graph = new DependencyGraph([Task("01-root"), Task("02-b", "01-root")]);

        Assert.Empty(graph.TransitiveDependenciesOf("01-root"));
    }
}
