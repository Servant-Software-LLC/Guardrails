using Guardrails.Core.Graph;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// #513 — a wave's entry/exit gate leaves must be INDEXED, or nothing can ever badge them. The diagram
/// always emitted a "Wave N Entry Gate" box; it was absent from the status-node index, and the overlay
/// gives no badge to a node it has no entry for. A gate that ran a whole-solution build and both suites
/// unfiltered therefore rendered exactly like one that never ran.
/// </summary>
public sealed class WaveGateBadgeAndLinkTests
{
    private static GuardrailDefinition Check(string name) =>
        new() { Name = name, Path = $"{name}.ps1", Kind = ActionKind.Script };

    private static PlanDefinition WavedPlan() => new()
    {
        PlanDirectory = "/plan",
        Workspace = "/repo",
        Config = new RunConfig { Version = 1 },
        Tasks = [],
        Waves =
        [
            new WaveNode
            {
                Number = 1, Dir = "wave-01-alpha", Slug = "alpha", Directory = "/plan/wave-01-alpha",
                Tasks = [],
                Preflights = [Check("01-entry-a"), Check("02-entry-b")],
                Guardrails = [Check("01-exit-a"), Check("02-exit-b"), Check("03-exit-c")]
            }
        ]
    };

    [Fact]
    public void WaveGateLeavesAreIndexed_SoTheOverlayCanBadgeThem()
    {
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(WavedPlan());

        Assert.Equal(2, nodes.WaveEntryGateLeaves.Count);
        Assert.Equal(3, nodes.WaveExitGateLeaves.Count);
    }

    [Fact]
    public void TheIndexedIdsMatchTheIdsTheRendererActuallyEmits()
    {
        // The whole mechanism is a lookup by node id. An index that agrees with itself but disagrees with
        // the emitted diagram badges nothing — and would look correct in any test that only reads the map.
        PlanDefinition plan = WavedPlan();
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);
        string mermaid = MermaidRenderer.Render(plan);

        foreach (string id in nodes.WaveEntryGateLeaves.Values.Concat(nodes.WaveExitGateLeaves.Values))
        {
            Assert.Contains(id, mermaid, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EntryAndExitLeavesAreDistinct_SoAPassingEntryCannotBadgeAFailingExit()
    {
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(WavedPlan());

        string[] entry = [.. nodes.WaveEntryGateLeaves.Values];
        string[] exit = [.. nodes.WaveExitGateLeaves.Values];

        Assert.Empty(entry.Intersect(exit, StringComparer.Ordinal));
        Assert.All(entry, id => Assert.Contains("_preflights_", id, StringComparison.Ordinal));
        Assert.All(exit, id => Assert.Contains("_guardrails_", id, StringComparison.Ordinal));
    }
}
