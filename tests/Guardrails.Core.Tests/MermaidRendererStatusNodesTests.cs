using Guardrails.Core.Graph;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="MermaidRenderer.StatusNodes"/> + <see cref="DiagramStatusNodes"/> (issue
/// #219, SSOT §10.1): the node-id surface the live status overlay uses to translate the observer's
/// SEMANTIC events (<c>task.Id</c>, <c>GuardrailResult.Name</c>) into the EXACT SVG node ids the
/// renderer emits. The load-bearing test is <see cref="StatusNodes_IsInBijection_WithEveryRenderedContainerAndLeafSvgId"/>:
/// a golden BIJECTION between <c>StatusNodes</c> and the ids the renderer actually draws, in BOTH
/// directions — the same anti-drift discipline <c>GraphSourceHashTests</c> applies to
/// <c>SemanticContent</c>. If the emitter's ordinal/id math ever diverges from <c>StatusNodes</c>' the
/// bijection breaks and this fails.
/// </summary>
public sealed class MermaidRendererStatusNodesTests
{
    private static GuardrailDefinition Check(string name) => new()
    {
        Name = name,
        Path = $"/fake/checks/{name}.sh",
        Kind = ActionKind.Script,
    };

    private static TaskNode TaskWith(
        string id,
        IReadOnlyList<GuardrailDefinition> preflights,
        IReadOnlyList<GuardrailDefinition> guardrails,
        params string[] dependsOn) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = $"task {id}",
        DependsOn = dependsOn,
        Action = new ActionDefinition { Path = $"/fake/tasks/{id}/action.sh", Kind = ActionKind.Script },
        Guardrails = guardrails,
        Preflights = preflights,
    };

    private static PlanDefinition PlanWith(
        IReadOnlyList<GuardrailDefinition> planPreflights,
        IReadOnlyList<GuardrailDefinition> planGuardrails,
        params TaskNode[] tasks) => new()
    {
        PlanDirectory = "/fake/plan",
        Workspace = "/fake",
        Config = new RunConfig { Version = 1 },
        Tasks = tasks,
        PlanPreflights = planPreflights,
        PlanGuardrails = planGuardrails,
    };

    /// <summary>A plan exercising every id family: plan-level pre/guardrails, task pre/guardrails, deps.</summary>
    private static PlanDefinition FullPlan() => PlanWith(
        [Check("01-baseline-green"), Check("02-clean-tree")],
        [Check("01-full-suite")],
        TaskWith("01-a", [Check("01-dep-delivered")], [Check("01-build"), Check("02-test")]),
        TaskWith("02-b", [], [Check("01-check")], "01-a"));

    // === the anti-drift bijection golden test ==========================================

    [Fact]
    public void StatusNodes_IsInBijection_WithEveryRenderedContainerAndLeafSvgId()
    {
        PlanDefinition plan = FullPlan();
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);

        HashSet<string> statusIds = AllStatusIds(nodes);
        HashSet<string> renderedIds = ExtractRenderedNodeIds(MermaidRenderer.Render(plan));

        // No rendered container/leaf id lacks a StatusNodes entry (the observer could never badge it)...
        Assert.Empty(renderedIds.Except(statusIds));
        // ...and no StatusNodes entry points at an id the renderer never draws (a dead/typo'd badge).
        Assert.Empty(statusIds.Except(renderedIds));
    }

    [Fact]
    public void StatusNodes_Bijection_HoldsForAPlanWithEmptyPlanLevelFolders()
    {
        // The two plan-level bracket containers are ALWAYS emitted (structural brackets), even with no
        // plan-level checks — so their container ids must still be present in both sets.
        PlanDefinition plan = PlanWith([], [], TaskWith("01-a", [], [Check("01-check")]));
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);

        HashSet<string> statusIds = AllStatusIds(nodes);
        HashSet<string> renderedIds = ExtractRenderedNodeIds(MermaidRenderer.Render(plan));

        Assert.Contains("plan_preflights", renderedIds);
        Assert.Contains("plan_guardrails", renderedIds);
        Assert.Empty(renderedIds.Except(statusIds));
        Assert.Empty(statusIds.Except(renderedIds));
    }

    // === id shape + key vocabulary =====================================================

    [Fact]
    public void TaskContainers_KeyedByTaskId_MapToTheContainerSubgraphId()
    {
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(FullPlan());

        Assert.Equal("task_01_a", nodes.TaskContainers["01-a"]);
        Assert.Equal("task_02_b", nodes.TaskContainers["02-b"]);
    }

    [Fact]
    public void GuardrailLeafIds_FollowTheNameSortedOrdinal_NotInputOrder()
    {
        // Supplied out of order (02-test before 01-build); the renderer sorts ordinal by Name, so
        // 01-build gets ordinal 0 (_gr_0) and 02-test ordinal 1 (_gr_1).
        PlanDefinition plan = PlanWith([], [],
            TaskWith("01-a", [], [Check("02-test"), Check("01-build")]));
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);

        Assert.Equal("task_01_a_gr_0", nodes.TaskGuardrailLeaves[("01-a", "01-build")]);
        Assert.Equal("task_01_a_gr_1", nodes.TaskGuardrailLeaves[("01-a", "02-test")]);
    }

    [Fact]
    public void PreflightLeafIds_UseThePfPrefix_KeyedByTaskIdAndName()
    {
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(FullPlan());

        Assert.Equal("task_01_a_pf_0", nodes.TaskPreflightLeaves[("01-a", "01-dep-delivered")]);
    }

    [Fact]
    public void PlanLevelLeafIds_UseThePlanBracketPrefixes_KeyedByName()
    {
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(FullPlan());

        Assert.Equal("plan_preflights_0", nodes.PlanPreflightLeaves["01-baseline-green"]);
        Assert.Equal("plan_preflights_1", nodes.PlanPreflightLeaves["02-clean-tree"]);
        Assert.Equal("plan_guardrails_0", nodes.PlanGuardrailLeaves["01-full-suite"]);
        Assert.Equal("plan_preflights", nodes.PlanPreflightsContainerId);
        Assert.Equal("plan_guardrails", nodes.PlanGuardrailsContainerId);
    }

    [Fact]
    public void StatusNodes_LeafIds_MatchTheClickTargets_RenderInteractiveEmits()
    {
        // The click directives RenderInteractive emits target the SAME leaf ids — proving StatusNodes
        // and the emitter agree beyond just the node lines (both derive from the shared OrdinalChecks).
        PlanDefinition plan = FullPlan();
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);
        string interactive = MermaidRenderer.RenderInteractive(plan);

        Assert.Contains($"click {nodes.TaskGuardrailLeaves[("01-a", "01-build")]} href", interactive, StringComparison.Ordinal);
        Assert.Contains($"click {nodes.TaskPreflightLeaves[("01-a", "01-dep-delivered")]} href", interactive, StringComparison.Ordinal);
        Assert.Contains($"click {nodes.PlanGuardrailLeaves["01-full-suite"]} href", interactive, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskContainers_UseTheSameSanitizedBaseAs_TheEmitter()
    {
        // A task id containing a character Sanitize rewrites ('.') must map to the SAME container id the
        // Mermaid source emits, so the overlay's DOM-id lookup succeeds.
        PlanDefinition plan = PlanWith([], [], TaskWith("01-a.b", [], [Check("01-check")]));
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);

        Assert.Equal("task_01_a_b", nodes.TaskContainers["01-a.b"]);
        Assert.Contains("subgraph task_01_a_b[", MermaidRenderer.Render(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void StatusNodes_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MermaidRenderer.StatusNodes(null!));
    }

    // === helpers =======================================================================

    private static HashSet<string> AllStatusIds(DiagramStatusNodes nodes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal)
        {
            nodes.PlanPreflightsContainerId,
            nodes.PlanGuardrailsContainerId,
        };
        foreach (string v in nodes.TaskContainers.Values) ids.Add(v);
        foreach (string v in nodes.TaskGuardrailLeaves.Values) ids.Add(v);
        foreach (string v in nodes.TaskPreflightLeaves.Values) ids.Add(v);
        foreach (string v in nodes.PlanPreflightLeaves.Values) ids.Add(v);
        foreach (string v in nodes.PlanGuardrailLeaves.Values) ids.Add(v);
        return ids;
    }

    /// <summary>
    /// Extract every CONTAINER id (<c>subgraph &lt;id&gt;[</c>) and LEAF check id
    /// (<c>&lt;id&gt;[label]:::preflight|guardrail</c>) the renderer draws — the ground truth the
    /// bijection is checked against. Deliberately parses the SAME emitted Mermaid the browser would
    /// decorate, not any internal helper, so a drift in the emitter shows up here.
    /// </summary>
    private static HashSet<string> ExtractRenderedNodeIds(string mermaid)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in mermaid.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("subgraph ", StringComparison.Ordinal))
            {
                int bracket = line.IndexOf('[', StringComparison.Ordinal);
                if (bracket > "subgraph ".Length)
                {
                    ids.Add(line["subgraph ".Length..bracket].Trim());
                }

                continue;
            }

            if (line.EndsWith(":::preflight", StringComparison.Ordinal)
                || line.EndsWith(":::guardrail", StringComparison.Ordinal))
            {
                int bracket = line.IndexOf('[', StringComparison.Ordinal);
                if (bracket > 0)
                {
                    ids.Add(line[..bracket].Trim());
                }
            }
        }

        return ids;
    }
}
