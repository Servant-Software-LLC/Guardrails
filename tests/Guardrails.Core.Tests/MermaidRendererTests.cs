using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="MermaidRenderer.Render"/> under the CONTAINER model (SSOT §10).
/// Pure string mapping — no disk, no processes; in-memory <see cref="PlanFixtures"/>. Each task is a
/// <c>subgraph task_&lt;id&gt;</c> holding its preflight/guardrail check nodes DIRECTLY inside it (no
/// nested <c>Preflights</c>/<c>Guardrails</c> wrapper subgraph — dropped as a simplification: the
/// wrapper was purely cosmetic, never referenced by edge emission, styling, or hashing); the DAG is
/// drawn <c>subgraph --&gt; subgraph</c> (container→container) so each edge clips to a container's
/// OUTER BORDER (issue #210) — there are NO interior invisible anchor nodes, NO <c>done_</c> reconvergence
/// node, and NO <c>task --&gt; guardrail</c> fan-out edge. Container fills are applied per-container via a
/// <c>style &lt;id&gt; …</c> statement (a <c>class</c> assignment does not reach an edge-endpoint subgraph in
/// the bundled Mermaid).
/// </summary>
public sealed class MermaidRendererTests
{
    // --- small helpers to build tasks with explicit guardrails -------------------------

    private static GuardrailDefinition Guardrail(string name, string? description = null) => new()
    {
        Name = name,
        Path = $"/fake/guardrails/{name}.sh",
        Kind = ActionKind.Script,
        Description = description
    };

    private static TaskNode TaskWith(string id, IReadOnlyList<GuardrailDefinition> guardrails, params string[] dependsOn) =>
        new()
        {
            Id = id,
            Directory = $"/fake/tasks/{id}",
            Description = $"fixture task {id}",
            DependsOn = dependsOn,
            Action = new ActionDefinition { Path = $"/fake/tasks/{id}/action.sh", Kind = ActionKind.Script },
            Guardrails = guardrails
        };

    private static TaskNode TaskWithPreflights(
        string id,
        IReadOnlyList<GuardrailDefinition> preflights,
        IReadOnlyList<GuardrailDefinition> guardrails,
        params string[] dependsOn) =>
        TaskWith(id, guardrails, dependsOn) with { Preflights = preflights };

    /// <summary>Split rendered Mermaid into trimmed, non-empty lines for set-based assertions.</summary>
    private static IReadOnlyList<string> Lines(string mermaid) =>
        mermaid.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    // === structure (container model) — RED against the current renderer ===============

    [Fact]
    public void Render_StartsWithFlowchartTd()
    {
        string mermaid = MermaidRenderer.Render(Plan(TaskWith("01-a", [Guardrail("01-check")])));
        Assert.Equal("flowchart TD", Lines(mermaid)[0]);
    }

    [Fact]
    public void Render_EmitsATaskContainerSubgraphPerTask()
    {
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-check")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a"))));

        // Container model: `subgraph task_<id>`, not a bare `task_<id>[...]` node.
        Assert.Contains(lines, l => l.StartsWith("subgraph task_01_a", StringComparison.Ordinal)
                                    && !l.StartsWith("subgraph task_01_a_", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("subgraph task_02_b", StringComparison.Ordinal)
                                    && !l.StartsWith("subgraph task_02_b_", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_DrawsGuardrailCheckDirectlyInsideTheTaskContainer_NoNestedGuardrailsSubgraph()
    {
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "Solution builds clean")]))));

        // No nested "Guardrails" (or "Preflights") wrapper subgraph — leaf check nodes are direct
        // children of the task container (dropped as a simplification: the wrapper was purely
        // cosmetic, never referenced by edge emission, container styling, or the source hash).
        Assert.DoesNotContain(lines, l => l.StartsWith("subgraph", StringComparison.Ordinal)
                                    && l.Contains("Guardrails", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("subgraph", StringComparison.Ordinal)
                                    && l.Contains("Preflights", StringComparison.Ordinal));
        Assert.Contains("01-build", string.Join("\n", lines));
    }

    [Fact]
    public void Render_DependencyEdge_IsDrawnContainerToContainer()
    {
        // 02-b dependsOn 01-a → edge task_01_a --> task_02_b (subgraph→subgraph, clipping to the
        // container border), NOT an anchor→anchor edge and NOT done_01_a --> task_02_b.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-check")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a"))));

        Assert.Contains("task_01_a --> task_02_b", lines);
        Assert.DoesNotContain(lines, l => l.Contains("_anchor", StringComparison.Ordinal));
        Assert.DoesNotContain("done_01_a --> task_02_b", lines);
    }

    [Fact]
    public void Render_EmitsNoInvisibleAnchorsAndStylesContainersById()
    {
        // Issue #210: the interior invisible anchors are gone (no anchor node, no classDef
        // invisible), and each container carries a `style <id> …` fill statement instead of a
        // `class <id> <className>;` assignment.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(TaskWith("01-a", [Guardrail("01-check")]))));

        Assert.DoesNotContain(lines, l => l.Contains("_anchor", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(":::invisible", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("classDef invisible", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("class ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("style task_01_a fill:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("style plan_preflights fill:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("style plan_guardrails fill:", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ContainsNoDoneNode()
    {
        string render = MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-check")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a")));

        Assert.DoesNotContain("done_", render);
    }

    [Fact]
    public void Render_ContainsNoTaskToGuardrailFanOutEdge()
    {
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build"), Guardrail("02-test")]))));

        // The old fan-out (task_<id> --> gr_<id>_<n>) is retired; checks live inside the container.
        Assert.DoesNotContain(lines, l => l.StartsWith("task_", StringComparison.Ordinal)
                                          && l.Contains(" --> gr_", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_SanitizesAwkwardTaskId_IntoASafeContainerId()
    {
        // An id with dots, slashes, spaces, and a quote — every non-alphanumeric → '_' in the node id,
        // while the literal id survives as a quoted, HTML-escaped label.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(TaskWith("a.b/c d\"e", [Guardrail("01-x")]))));

        Assert.Contains(lines, l => l.StartsWith("subgraph task_a_b_c_d_e", StringComparison.Ordinal));
        Assert.Contains("a.b/c d&quot;e", string.Join("\n", lines));
    }

    // === label safety (model-neutral) — green in both models ===========================

    [Fact]
    public void Render_GuardrailWithDescription_StillUsesNameAsDrawnLabel_NotTheDescription()
    {
        // Issue #222: the drawn label is ALWAYS the check's Name, regardless of whether a
        // description is present. A prior version preferred Description when present; that
        // preference is gone.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "Solution builds clean")]))));

        Assert.DoesNotContain("Solution builds clean", string.Join("\n", lines));
        Assert.Contains(lines, l => l.Contains("[\"01-build\"]", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_GuardrailWithoutDescription_UsesNameAsDrawnLabel()
    {
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: null)]))));

        Assert.Contains(lines, l => l.Contains("[\"01-build\"]", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_GuardrailNameWithHtmlChars_IsHtmlEscaped()
    {
        // Mermaid renders labels as HTML: < / > would silently drop text as stray tags, & must be escaped
        // first (no double-escaping), and # can trigger entity parsing. The escaping (Quote()) is a
        // shared helper applied to whatever string ends up as the drawn label — always Name now
        // (issue #222) — so this exercises the same code path with Name as the carrier.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("a < b && c > d #1")]))));

        Assert.Contains("a &lt; b &amp;&amp; c &gt; d &#35;1", string.Join("\n", lines));
    }

    [Fact]
    public void Render_GuardrailDescriptionWithNewlines_TooltipCollapsesToSingleSafeLine()
    {
        // A check's Name (file-derived) can never contain a raw newline, so this scenario can only
        // arise in a Description now that the drawn label is always Name (issue #222) — it moved to
        // the click tooltip (RenderInteractive), the one place a description still surfaces. A raw
        // newline in a Mermaid `click` directive would break the WHOLE diagram; the renderer
        // collapses \r\n / \r / \n to single spaces there too.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.RenderInteractive(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "line one\r\nline two\rline three\nline four")]))));

        Assert.Contains(lines, l => l.StartsWith("click", StringComparison.Ordinal)
            && l.Contains("line one line two line three line four", StringComparison.Ordinal));
    }

    // === emission-order contract (preflights before guardrails, no nested boxes) =======

    [Fact]
    public void Render_TaskWithBothPreflightAndGuardrail_EmitsPreflightNodeBeforeGuardrailNode()
    {
        // The removed nested boxes used to convey "preflights run before guardrails" visually;
        // with them gone, source order is the contractual replacement — tested here directly.
        GuardrailDefinition preflight = Guardrail("01-dep-delivered", description: "dependency delivered");
        GuardrailDefinition guardrail = Guardrail("01-build", description: "builds clean");

        string render = MermaidRenderer.Render(Plan(
            TaskWithPreflights("01-a", [preflight], [guardrail])));

        int preflightIndex = render.IndexOf("01-dep-delivered", StringComparison.Ordinal);
        int guardrailIndex = render.IndexOf("01-build", StringComparison.Ordinal);

        Assert.True(preflightIndex >= 0 && guardrailIndex >= 0);
        Assert.True(preflightIndex < guardrailIndex,
            "the task-level preflight node must be emitted before the guardrail node within the same container");
    }

    [Fact]
    public void Render_TaskLevelPreflight_IsDrawnDirectlyInsideTheTaskContainer_NoWrapperSubgraph()
    {
        GuardrailDefinition preflight = Guardrail("01-dep-delivered", description: "dependency delivered");
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWithPreflights("01-a", [preflight], [Guardrail("01-build")]))));

        Assert.DoesNotContain(lines, l => l.StartsWith("subgraph", StringComparison.Ordinal)
                                          && l.Contains("Preflights", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(":::preflight", StringComparison.Ordinal));
    }

    // === every check's drawn label is its Name; full Description lives in the click tooltip =====
    // (issue #222 — a prior version truncated a task-level preflight's Description into the drawn
    // label instead; the owner asked for the short, stable Name every check's click target already
    // opens, uniformly for every check kind at both scopes, so no truncation heuristic is needed.)

    [Fact]
    public void Render_LongPreflightDescription_DrawnLabelIsTheNameNotTheDescription()
    {
        const string longDescription =
            "Task-level JIT dependency-delivery precondition, keyed to the 04 -> 05 dependsOn edge. "
            + "Verifies that task 04 actually threaded RequestId into the inherited source at 05's "
            + "taskBase, BEFORE 05's action runs. A deterministic byte-check (no live probe).";
        GuardrailDefinition preflight = Guardrail("01-dep-delivered", description: longDescription);

        string render = MermaidRenderer.Render(Plan(
            TaskWithPreflights("01-a", [preflight], [Guardrail("01-build")])));

        Assert.DoesNotContain(longDescription, render);
        Assert.DoesNotContain("taskBase", render);
        Assert.Contains("01-dep-delivered", render);
    }

    [Fact]
    public void Render_GuardrailWithLongDescription_DrawnLabelIsAlsoTheNameNotTheDescription()
    {
        // Not just task-level preflights — EVERY check kind (plan-level or task-level, preflight or
        // guardrail) draws its Name, never its Description, regardless of description length.
        const string longDescription =
            "Structural check that the barrier test's four load-bearing assertions survive verbatim "
            + "after the hardening edit, so a fix cannot quietly weaken them to tolerate a race.";
        GuardrailDefinition guardrail = Guardrail("02-assertions-not-weakened", description: longDescription);

        string render = MermaidRenderer.Render(Plan(TaskWith("01-a", [guardrail])));

        Assert.DoesNotContain(longDescription, render);
        Assert.Contains("02-assertions-not-weakened", render);
    }

    [Fact]
    public void RenderInteractive_LongDescription_IsRecoverableViaClickTooltip()
    {
        // The drawn label (the Name) does not lose the full description — it remains reachable via
        // the SAME click mechanism RenderInteractive already uses for every node (issue #33): the
        // node's `click` directive tooltip carries the full description.
        const string longDescription =
            "Task-level JIT dependency-delivery precondition, keyed to the 04 -> 05 dependsOn edge. "
            + "Verifies that task 04 actually threaded RequestId into the inherited source at 05's "
            + "taskBase, BEFORE 05's action runs. A deterministic byte-check (no live probe).";
        GuardrailDefinition preflight = Guardrail("01-dep-delivered", description: longDescription);

        string interactive = MermaidRenderer.RenderInteractive(Plan(
            TaskWithPreflights("01-a", [preflight], [Guardrail("01-build")])));

        IReadOnlyList<string> lines = Lines(interactive);
        string nodeLabelLine = Assert.Single(lines, l => l.Contains(":::preflight", StringComparison.Ordinal));
        string clickLine = Assert.Single(lines, l => l.StartsWith("click", StringComparison.Ordinal)
                                                     && l.Contains("_pf_0", StringComparison.Ordinal));

        // The drawn node label is the short Name...
        Assert.DoesNotContain(longDescription, nodeLabelLine);
        Assert.Contains("01-dep-delivered", nodeLabelLine);
        // ...but the SAME node's click tooltip carries the full description.
        Assert.Contains("taskBase", clickLine);
    }

    // === task-container clicks: no Mermaid-source mechanism at all (issue #211 anchor-node =====
    // === mechanism superseded; issue #235 moves the click target to a post-render SVG overlay) =
    //
    // Real headless-Chrome verification against the bundled mermaid@11.4.1 proved a `click`
    // directive targeting a subgraph/cluster id never fires: Mermaid wraps a clickable LEAF node
    // in a real <a href> element, but never wraps a <g class="cluster"> (subgraph) in one. Issue
    // #211's first fix (a dedicated invisible anchor NODE, RenderInteractive-only) proved USELESS
    // in practice — real headless-Chrome measurement on a real 4-guardrail task container found
    // dagre packed the anchor into a sliver covering only 0.44% of the container's area, missed by
    // every realistic click point, with "dead-center" instead landing on a leaf guardrail box's own
    // click target. The anchor-node mechanism is REMOVED entirely; MermaidRenderer no longer emits
    // any container click directive, anchor node, or `invisible` classDef — HtmlDiagramRenderer's
    // post-render JS overlay (see HtmlDiagramRendererTests) now owns the container click target,
    // fed by MermaidRenderer.TaskFolderTargets.

    [Fact]
    public void RenderInteractive_EmitsNoContainerClickDirective_ForTaskContainer()
    {
        string interactive = MermaidRenderer.RenderInteractive(Plan(TaskWith("01-a", [Guardrail("01-check")])));
        IReadOnlyList<string> lines = Lines(interactive);

        // Neither the bare container id (never fired anyway) nor the retired anchor-node click.
        Assert.DoesNotContain(lines, l => l.StartsWith("click task_01_a href", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("click task_01_a_anchor", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("_anchor", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderInteractive_NoLongerDeclaresAnyAnchorNode_OrInvisibleClassDef()
    {
        string interactive = MermaidRenderer.RenderInteractive(Plan(
            TaskWithPreflights("01-a", [Guardrail("01-pf", description: "pf")], [Guardrail("01-gr"), Guardrail("02-gr")])));
        IReadOnlyList<string> lines = Lines(interactive);

        Assert.DoesNotContain(lines, l => l.Contains("_anchor", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(":::invisible", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("classDef invisible", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderInteractive_And_Render_EmitIdenticalContainerShape()
    {
        // With the anchor-node mechanism gone, RenderInteractive's container/node shape (ignoring
        // the trailing click directives it appends) is now byte-identical to Render's — the two no
        // longer diverge on a per-task extra node.
        PlanDefinition plan = Plan(
            TaskWithPreflights("01-a", [Guardrail("01-pf", description: "pf")], [Guardrail("01-gr"), Guardrail("02-gr")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a"));

        string clean = MermaidRenderer.Render(plan);
        string interactive = MermaidRenderer.RenderInteractive(plan);

        // Every line in the clean render must also appear in the interactive render (the
        // interactive render only ADDS click lines; it never changes existing lines).
        foreach (string line in Lines(clean))
        {
            Assert.Contains(line, Lines(interactive));
        }
    }

    [Fact]
    public void RenderInteractive_AnchorFreeContainerToContainer_EdgesUnaffected()
    {
        // The #210 container->container DAG edges are unaffected by the removal.
        string interactive = MermaidRenderer.RenderInteractive(Plan(
            TaskWith("01-a", [Guardrail("01-check")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a")));
        IReadOnlyList<string> lines = Lines(interactive);

        Assert.Contains("task_01_a --> task_02_b", lines);
        Assert.DoesNotContain(lines, l => l.Contains("_anchor", StringComparison.Ordinal) && l.Contains("-->", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_NeverDeclaresAnAnchorNode_OrTheInvisibleClassDef()
    {
        // The clean Render output (diagram.md / SemanticContent / the staleness hash) must never
        // gain an interior anchor node — issue #210 deliberately removed the original one, and
        // issue #235 removed the #211 replacement entirely (RenderInteractive never emits it either
        // now).
        string clean = MermaidRenderer.Render(Plan(TaskWith("01-a", [Guardrail("01-check")])));

        Assert.DoesNotContain(Lines(clean), l => l.Contains("_anchor", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(clean), l => l.StartsWith("classDef invisible", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderInteractive_LeafGuardrailClick_StillTargetsTheLeafNodeItself()
    {
        // Leaf check-node clicks are completely unaffected by the container-click rework — Mermaid
        // DOES wrap a clickable leaf node in a real <a href>, confirmed by real headless-Chrome
        // verification.
        string interactive = MermaidRenderer.RenderInteractive(Plan(TaskWith("01-a", [Guardrail("01-check")])));

        Assert.Contains(Lines(interactive), l => l.StartsWith("click task_01_a_gr_0 href", StringComparison.Ordinal));
    }

    // === MermaidRenderer.TaskFolderTargets (issue #235: feeds the HtmlDiagramRenderer overlay) ==

    [Fact]
    public void TaskFolderTargets_MapsContainerId_ToPlanRelativeFolderPath_WithTrailingSlash()
    {
        IReadOnlyDictionary<string, string> targets =
            MermaidRenderer.TaskFolderTargets(Plan(TaskWith("01-a", [Guardrail("01-check")])));

        // Fixture paths (/fake/plan, /fake/tasks/01-a) are siblings, not nested — so the
        // plan-relative path is "../tasks/01-a/" here; the real CLI plan layout nests tasks/
        // under the plan folder, where the equivalent path has no "../" (see GraphHtmlCliTests).
        Assert.True(targets.TryGetValue("task_01_a", out string? path));
        Assert.Equal("../tasks/01-a/", path);
        Assert.DoesNotContain('\\', path); // forward slashes only — byte-identical across OSes
    }

    [Fact]
    public void TaskFolderTargets_UsesSameNodeIdBaseAs_AppendNodesAndEdges()
    {
        // A task id containing characters Sanitize() rewrites (e.g. '.') must map to the SAME
        // container id the Mermaid source actually emits, so the overlay script's lookup by DOM id
        // succeeds.
        PlanDefinition plan = Plan(TaskWith("01-a.b", [Guardrail("01-check")]));
        IReadOnlyDictionary<string, string> targets = MermaidRenderer.TaskFolderTargets(plan);

        string interactive = MermaidRenderer.RenderInteractive(plan);
        string containerLine = Assert.Single(Lines(interactive), l => l.StartsWith("subgraph task_", StringComparison.Ordinal));
        string containerId = containerLine.Split('[')[0].Replace("subgraph ", "", StringComparison.Ordinal);

        Assert.True(targets.ContainsKey(containerId), $"TaskFolderTargets must key by the same container id ({containerId}) the Mermaid source emits");
    }

    [Fact]
    public void TaskFolderTargets_OneEntryPerTask_NoPlanLevelContainers()
    {
        IReadOnlyDictionary<string, string> targets = MermaidRenderer.TaskFolderTargets(Plan(
            TaskWith("01-a", [Guardrail("01-check")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a")));

        Assert.Equal(2, targets.Count);
        Assert.DoesNotContain("plan_preflights", targets.Keys);
        Assert.DoesNotContain("plan_guardrails", targets.Keys);
    }

    [Fact]
    public void TaskFolderTargets_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MermaidRenderer.TaskFolderTargets(null!));
    }

    // === contracts that must survive the rewrite (model-neutral) =======================

    [Fact]
    public void Render_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MermaidRenderer.Render(null!));
    }

    /// <summary>
    /// The renderer must emit LF-only line breaks on EVERY OS (issue #3): both the styled
    /// <see cref="MermaidRenderer.Render"/> output and the hashed <see cref="MermaidRenderer.SemanticContent"/>
    /// must be free of any <c>'\r'</c>.
    /// </summary>
    [Fact]
    public void RenderAndSemanticContent_ContainNoCarriageReturn()
    {
        PlanDefinition plan = Plan(
            TaskWith("01-a", [Guardrail("01-build"), Guardrail("02-test")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a"));

        Assert.DoesNotContain('\r', MermaidRenderer.Render(plan));
        Assert.DoesNotContain('\r', MermaidRenderer.SemanticContent(plan));
    }
}
