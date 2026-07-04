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
        Assert.Contains("Solution builds clean", string.Join("\n", lines));
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
    public void Render_GuardrailWithDescription_UsesDescriptionAsDrawnLabel()
    {
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "Solution builds clean")]))));

        Assert.Contains("Solution builds clean", string.Join("\n", lines));
        // The bare Name is NOT used as a drawn label when a description is present.
        Assert.DoesNotContain(lines, l => l.Contains("[\"01-build\"]", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_GuardrailWithoutDescription_UsesNameAsDrawnLabel()
    {
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: null)]))));

        Assert.Contains(lines, l => l.Contains("[\"01-build\"]", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_GuardrailDescriptionWithHtmlChars_IsHtmlEscaped()
    {
        // Mermaid renders labels as HTML: < / > would silently drop text as stray tags, & must be escaped
        // first (no double-escaping), and # can trigger entity parsing.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "a < b && c > d #1")]))));

        Assert.Contains("a &lt; b &amp;&amp; c &gt; d &#35;1", string.Join("\n", lines));
    }

    [Fact]
    public void Render_GuardrailDescriptionWithNewlines_CollapsesToSingleSafeLine()
    {
        // A raw newline in a Mermaid label would break the WHOLE diagram; the renderer collapses
        // \r\n / \r / \n to single spaces.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "line one\r\nline two\rline three\nline four")]))));

        Assert.Contains("line one line two line three line four", string.Join("\n", lines));
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

        int preflightIndex = render.IndexOf("dependency delivered", StringComparison.Ordinal);
        int guardrailIndex = render.IndexOf("builds clean", StringComparison.Ordinal);

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

    // === preflight label truncation + click-for-detail =================================

    [Fact]
    public void Render_LongPreflightDescription_IsTruncatedInTheDrawnLabel()
    {
        const string longDescription =
            "Task-level JIT dependency-delivery precondition, keyed to the 04 -> 05 dependsOn edge. "
            + "Verifies that task 04 actually threaded RequestId into the inherited source at 05's "
            + "taskBase, BEFORE 05's action runs. A deterministic byte-check (no live probe).";
        GuardrailDefinition preflight = Guardrail("01-dep-delivered", description: longDescription);

        string render = MermaidRenderer.Render(Plan(
            TaskWithPreflights("01-a", [preflight], [Guardrail("01-build")])));

        Assert.DoesNotContain(longDescription, render);
        Assert.DoesNotContain("taskBase", render); // well past the truncation budget
    }

    [Fact]
    public void Render_ShortPreflightDescription_IsNotTruncated()
    {
        GuardrailDefinition preflight = Guardrail("01-dep-delivered", description: "JIT dependency delivered");

        string render = MermaidRenderer.Render(Plan(
            TaskWithPreflights("01-a", [preflight], [Guardrail("01-build")])));

        Assert.Contains("JIT dependency delivered", render);
    }

    [Fact]
    public void RenderInteractive_LongPreflightDescription_IsRecoverableViaClickTooltip()
    {
        // The truncated NODE LABEL must not lose the full text — it must be reachable via the SAME
        // click mechanism RenderInteractive already uses for every node (issue #33): the node's
        // `click` directive tooltip carries the full description even though its drawn label doesn't.
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

        // The drawn node label is truncated...
        Assert.DoesNotContain(longDescription, nodeLabelLine);
        Assert.DoesNotContain("taskBase", nodeLabelLine);
        // ...but the SAME node's click tooltip carries the full description.
        Assert.Contains("taskBase", clickLine);
    }

    // === task-container click targets a click-only anchor, not the subgraph (issue #211) ==
    //
    // Real headless-Chrome verification against the bundled mermaid@11.4.1 proved a `click`
    // directive targeting a subgraph/cluster id never fires: Mermaid wraps a clickable LEAF node
    // in a real <a href> element, but never wraps a <g class="cluster"> (subgraph) in one. This
    // matches Mermaid's own still-open upstream limitation (mermaid-js/mermaid#1637, #5428). The
    // fix targets a dedicated invisible anchor node instead — RenderInteractive only; the clean
    // Render output (diagram.md / the staleness hash) must stay exactly as issue #210 left it.

    [Fact]
    public void RenderInteractive_TaskContainerClick_TargetsAnAnchorNode_NotTheContainerId()
    {
        string interactive = MermaidRenderer.RenderInteractive(Plan(TaskWith("01-a", [Guardrail("01-check")])));
        IReadOnlyList<string> lines = Lines(interactive);

        // The container's own click directive must NOT target the bare container id (that never
        // fires against a real Mermaid subgraph/cluster element) — it must target its anchor.
        Assert.DoesNotContain(lines, l => l.StartsWith("click task_01_a href", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("click task_01_a_anchor href", StringComparison.Ordinal)
                                    && l.Contains("\"01-a\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderInteractive_DeclaresOneInvisibleAnchorNode_AsTheLastLineInsideEachTaskContainer()
    {
        string interactive = MermaidRenderer.RenderInteractive(Plan(
            TaskWithPreflights("01-a", [Guardrail("01-pf", description: "pf")], [Guardrail("01-gr"), Guardrail("02-gr")])));
        IReadOnlyList<string> lines = Lines(interactive);

        int anchorIndex = lines.ToList().FindIndex(l => l == "task_01_a_anchor[\" \"]:::invisible");
        int endIndex = lines.ToList().FindIndex(anchorIndex + 1, l => l == "end");
        int lastGuardrailIndex = lines.ToList().FindLastIndex(l => l.Contains(":::guardrail", StringComparison.Ordinal));
        int lastPreflightIndex = lines.ToList().FindLastIndex(l => l.Contains(":::preflight", StringComparison.Ordinal));

        Assert.True(anchorIndex >= 0, "task container must declare its click-only anchor node");
        // The anchor is the LAST line inside the block (right before `end`) — after every check
        // node — so it never disturbs the preflight-before-guardrail emission-order contract.
        Assert.True(endIndex == anchorIndex + 1, "the anchor must be the line immediately before `end`");
        Assert.True(anchorIndex > lastGuardrailIndex && anchorIndex > lastPreflightIndex,
            "the anchor must be emitted after every check node in the container");
    }

    [Fact]
    public void RenderInteractive_AnchorNode_CarriesNoEdge()
    {
        // The #210 container->container DAG edges are UNCHANGED by the click-anchor fix: they
        // still attach to the container's own subgraph id, never to the anchor.
        string interactive = MermaidRenderer.RenderInteractive(Plan(
            TaskWith("01-a", [Guardrail("01-check")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a")));
        IReadOnlyList<string> lines = Lines(interactive);

        Assert.Contains("task_01_a --> task_02_b", lines);
        Assert.DoesNotContain(lines, l => l.Contains("_anchor", StringComparison.Ordinal) && l.Contains("-->", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderInteractive_DeclaresInvisibleClassDef()
    {
        string interactive = MermaidRenderer.RenderInteractive(Plan(TaskWith("01-a", [Guardrail("01-check")])));

        Assert.Contains(Lines(interactive), l => l.StartsWith("classDef invisible", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_NeverDeclaresAnAnchorNode_OrTheInvisibleClassDef()
    {
        // The clean Render output (diagram.md / SemanticContent / the staleness hash) must never
        // gain an interior anchor node again — issue #210 deliberately removed it, and the #211
        // click-only anchor is RenderInteractive-only.
        string clean = MermaidRenderer.Render(Plan(TaskWith("01-a", [Guardrail("01-check")])));

        Assert.DoesNotContain(Lines(clean), l => l.Contains("_anchor", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(clean), l => l.StartsWith("classDef invisible", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderInteractive_LeafGuardrailClick_StillTargetsTheLeafNodeItself()
    {
        // Leaf check-node clicks are unaffected by the #211 fix — Mermaid DOES wrap a clickable
        // leaf node in a real <a href>, confirmed by real headless-Chrome verification.
        string interactive = MermaidRenderer.RenderInteractive(Plan(TaskWith("01-a", [Guardrail("01-check")])));

        Assert.Contains(Lines(interactive), l => l.StartsWith("click task_01_a_gr_0 href", StringComparison.Ordinal));
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
