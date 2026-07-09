using Guardrails.Core.Graph;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="MermaidRenderer.StatusNodes"/> + <see cref="DiagramStatusNodes"/> (issue
/// #219, SSOT §10.1): the node-id surface the live status overlay uses to translate the observer's
/// SEMANTIC events (<c>task.Id</c>, <c>GuardrailResult.Name</c>) into the EXACT SVG node ids the
/// renderer emits. The load-bearing test is <see cref="StatusNodeIds_SetEqual_RenderedNodeIds"/>: it
/// asserts SET EQUALITY between the id SET <c>StatusNodes</c> claims and the id SET the renderer
/// actually draws — no StatusNodes entry points at an id the renderer never draws, and no rendered
/// container/leaf id lacks a StatusNodes entry — the same anti-drift discipline
/// <c>GraphSourceHashTests</c> applies to <c>SemanticContent</c>. If the emitter's ordinal/id math ever
/// diverges from <c>StatusNodes</c>' (e.g. a renamed prefix or a changed sort) set-equality breaks and
/// this fails.
/// <para>
/// <b>The two #332 ambiguities this SET-equality test cannot see are now foreclosed upstream.</b> Because
/// both sides are <see cref="HashSet{T}"/>s, two DISTINCT entities that collapse onto one id still dedupe
/// here and pass — so the guarantee they never reach a validated plan is enforced elsewhere: (A) a duplicate
/// check <c>Name</c> within one folder (e.g. <c>01-build.ps1</c> + <c>01-build.sh</c> both → Name "01-build")
/// is now a load-time validation ERROR (GR2035, proven by
/// <see cref="DuplicateCheckName_InOneFolder_IsRejectedByValidator"/>); (B) a task id equal to another task's
/// derived leaf id (task <c>a</c>'s guardrail → <c>task_a_gr_0</c> vs a task folder <c>a-gr-0</c> → container
/// <c>task_a_gr_0</c>) can no longer emit a duplicate DOM id, because <c>AllocateNodeIdBases</c> reserves the
/// derived leaf-id namespace (proven by <see cref="TaskIdCollidingWithDerivedLeafId_GetsDistinctStatusIds"/>).
/// </para>
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

    // === the anti-drift set-equality golden test =======================================

    [Fact]
    public void StatusNodeIds_SetEqual_RenderedNodeIds()
    {
        PlanDefinition plan = FullPlan();
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);

        HashSet<string> statusIds = AllStatusIds(nodes);
        HashSet<string> renderedIds = ExtractRenderedNodeIds(MermaidRenderer.Render(plan));

        // SET EQUALITY (the two set-difference directions): no rendered container/leaf id lacks a
        // StatusNodes entry (the observer could never badge it)...
        Assert.Empty(renderedIds.Except(statusIds));
        // ...and no StatusNodes entry points at an id the renderer never draws (a dead/typo'd badge).
        Assert.Empty(statusIds.Except(renderedIds));

        // Note (issue #332): this is SET equality, NOT a 1-to-1 bijection — both sides are HashSets, so two
        // DISTINCT entities that collapse onto one id would still dedupe and pass here. That is why #332 is
        // foreclosed upstream instead: a duplicate check Name in one folder is a load-time error (GR2035),
        // and a task-id ↔ leaf-id collision can no longer emit a duplicate DOM id (AllocateNodeIdBases).
    }

    [Fact]
    public void StatusNodeIds_SetEqual_RenderedNodeIds_ForEmptyPlanLevelFolders()
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

    [Fact]
    public void DuplicateCheckName_InOneFolder_IsRejectedByValidator()
    {
        // Issue #332 Scenario A, now FIXED (re-pointed from the old characterization test). Two checks in
        // ONE folder that share a Name — the real load-clean case is 01-build.ps1 + 01-build.sh, both →
        // GuardrailDefinition.Name "01-build". Such a plan USED to load clean and silently collapse the
        // (taskId, Name) key on this surface; it is now a hard validation ERROR (GR2035), so a loaded plan
        // can never present the ambiguity to StatusNodes. This is the executable proof #332 Scenario A is
        // fixed.
        var build1 = new GuardrailDefinition { Name = "01-build", Path = "/fake/tasks/01-a/guardrails/01-build.ps1", Kind = ActionKind.Script };
        var build2 = new GuardrailDefinition { Name = "01-build", Path = "/fake/tasks/01-a/guardrails/01-build.sh", Kind = ActionKind.Script };
        PlanDefinition plan = PlanWith([], [], TaskWith("01-a", [], [build1, build2]));

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(plan);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.DuplicateCheckName);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void TaskIdCollidingWithDerivedLeafId_GetsDistinctStatusIds()
    {
        // Issue #332 Scenario B, now FIXED: task 'a' (one guardrail) → leaf task_a_gr_0; a task folder
        // 'a-gr-0' → container task_a_gr_0 pre-fix (one DOM id shared by two nodes). AllocateNodeIdBases now
        // reserves the derived leaf-id namespace, so the two map to DISTINCT DOM ids on the status surface.
        PlanDefinition plan = PlanWith([], [],
            TaskWith("a", [], [Check("01-x")]),
            TaskWith("a-gr-0", [], [Check("01-y")]));
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);

        string leafOfA = nodes.TaskGuardrailLeaves[("a", "01-x")];
        string containerOfAGr0 = nodes.TaskContainers["a-gr-0"];

        Assert.Equal("task_a_gr_0", leafOfA);
        Assert.NotEqual(leafOfA, containerOfAGr0); // the collision is gone: the container is a bumped, distinct id.
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
