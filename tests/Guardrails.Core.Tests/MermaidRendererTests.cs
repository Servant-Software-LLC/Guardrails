using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="MermaidRenderer.Render"/> under the CONTAINER model (SSOT §10).
/// Pure string mapping — no disk, no processes; in-memory <see cref="PlanFixtures"/>. Each task is a
/// <c>subgraph task_&lt;id&gt;</c> holding its checks in nested <c>Preflights</c>/<c>Guardrails</c> subgraphs;
/// the DAG is drawn <c>subgraph --&gt; subgraph</c> (container→container) so each edge clips to a container's
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
    public void Render_DrawsGuardrailCheckInsideANestedGuardrailsSubgraph()
    {
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "Solution builds clean")]))));

        // A nested Guardrails subgraph exists (container model) and the check's drawn label appears in it.
        Assert.Contains(lines, l => l.StartsWith("subgraph", StringComparison.Ordinal)
                                    && l.Contains("Guardrails", StringComparison.Ordinal));
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
