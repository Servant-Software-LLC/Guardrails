using Guardrails.Core.Graph;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="HtmlDiagramRenderer.Render"/> (issue #33): the self-contained local
/// <c>diagram.html</c> viewer. Pure string mapping — no disk. Pins the provenance line, the
/// verbatim-source embedding, the interaction prerequisites (loose security for clicks, pan/zoom,
/// fullscreen), determinism, and newline normalization.
/// </summary>
public sealed class HtmlDiagramRendererTests
{
    private const string Hash = "abc123def456";
    private const string Source = "flowchart TD\n  task_a[\"a\"]:::task\n  classDef task fill:#cfe8ff;";

    private static readonly IReadOnlyDictionary<string, string> NoTargets =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> OneTarget =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["task_01_a"] = "tasks/01-a/" };

    [Fact]
    public void Render_FirstLine_IsProvenanceComment_WithSameHash()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);
        string firstLine = html.Split('\n')[0];

        // Same provenance shape as diagram.md, and FIRST so the \A-anchored reader (graph --check)
        // matches it in both files.
        Assert.Equal($"<!-- guardrails:graph v1 source-sha256={Hash} -->", firstLine);
    }

    [Fact]
    public void Render_EmbedsSourceVerbatim_InRawTextScript()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);

        Assert.Contains("type=\"text/plain\"", html);
        Assert.Contains(Source, html); // verbatim — not interpolated into a JS string
        Assert.Contains("<!doctype html>", html);
    }

    [Fact]
    public void Render_EnablesClicksAndNavigation()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);

        // Clicks (node -> source file) only fire when mermaid runs in 'loose' security.
        Assert.Contains("securityLevel: 'loose'", html);
        // Pan/zoom + fullscreen affordances.
        Assert.Contains("svg-pan-zoom", html);
        Assert.Contains("requestFullscreen", html);
    }

    [Fact]
    public void Render_RaisesMermaidSizeCeilings_SoLargeDagsRender()
    {
        // Issue #108: a large plan's DAG source exceeds Mermaid's default 50 000-char maxTextSize
        // and/or 500-edge maxEdges ceiling, so mermaid.render throws and the page falls back to
        // "could not render". The fix lifts BOTH ceilings in mermaid.initialize — assert they are
        // present (a regression that drops either re-introduces the big-plan render failure).
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);

        Assert.Contains("maxTextSize: 5000000", html);
        Assert.Contains("maxEdges: 100000", html);
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        Assert.Equal(
            HtmlDiagramRenderer.Render(Source, Hash, OneTarget),
            HtmlDiagramRenderer.Render(Source, Hash, OneTarget));
    }

    [Fact]
    public void Render_NormalizesCrlf_NoCarriageReturnsInOutput()
    {
        string html = HtmlDiagramRenderer.Render("flowchart TD\r\n  a-->b\r\n", Hash, NoTargets);
        Assert.DoesNotContain('\r', html);
    }

    [Fact]
    public void Render_EmptyHash_Throws()
    {
        Assert.Throws<ArgumentException>(() => HtmlDiagramRenderer.Render(Source, "", NoTargets));
    }

    [Fact]
    public void Render_NullTaskFolderTargets_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HtmlDiagramRenderer.Render(Source, Hash, null!));
    }

    // === legend overlay (outside the Mermaid graph; see MermaidRenderer remarks) =======

    [Fact]
    public void Render_IncludesALegendOverlayDiv_OutsideTheEmbeddedMermaidSource()
    {
        // A Mermaid-native legend was prototyped and rendered broken headless (dagre treats a
        // disconnected subgraph as a phantom extra "task"); the only working placement is an HTML
        // overlay div outside the embedded Mermaid <script> source, mirroring #bar/#hint.
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);

        Assert.Contains("id=\"legend\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_LegendOverlay_StatesBothAColorAndABeforeAfterTimingWord()
    {
        // A bare category name would not preserve the "preflights run before, guardrails run
        // after" semantic the removed nested boxes used to convey visually — the legend must spell
        // out both the colour mapping AND the timing/consequence in words.
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);

        Assert.Contains("Preflight", html, StringComparison.Ordinal);
        Assert.Contains("Guardrail", html, StringComparison.Ordinal);
        Assert.Contains("BEFORE", html, StringComparison.Ordinal);
        Assert.Contains("AFTER", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_LegendOverlay_IsNotInsideTheEmbeddedGraphSourceScript()
    {
        // The legend must live OUTSIDE the raw-text <script id="graph-source"> element — it is
        // not part of the Mermaid source, so it can never affect anything that parses that source
        // (rendering) or hashes it (GraphSourceHash, via MermaidRenderer.SemanticContent).
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);

        int scriptStart = html.IndexOf("id=\"graph-source\"", StringComparison.Ordinal);
        int scriptEnd = html.IndexOf("</script>", scriptStart, StringComparison.Ordinal);
        string scriptContent = html[scriptStart..scriptEnd];

        Assert.DoesNotContain("legend", scriptContent, StringComparison.Ordinal);
    }

    // === task-container title-band overlay (issue #232/#233 superseded, issue #235) ====
    //
    // The Mermaid-source anchor-node mechanism (#211) proved useless in practice — a real
    // headless-Chrome measurement found the anchor covering only 0.44% of a content-dense
    // container's area, off-center, missed by every realistic click point. The fix instead
    // injects a post-render SVG overlay on the container's title/label band via JavaScript.

    [Fact]
    public void Render_EmbedsTaskFolderTargets_AsJsonSideTable()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("id=\"task-folder-targets\"", html, StringComparison.Ordinal);
        Assert.Contains("\"task_01_a\"", html, StringComparison.Ordinal);
        Assert.Contains("tasks/01-a/", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NoLongerEmitsAnchorNodeMechanism()
    {
        // The #211 anchor-node click mechanism (Mermaid-source invisible node + classDef) is
        // superseded entirely — HtmlDiagramRenderer must not reference it either.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.DoesNotContain("classDef invisible", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OverlayScript_AppendsToCluster_NotInsertsFirst()
    {
        // Load-bearing z-order fix: appendChild (not insertBefore as firstChild) — see class
        // remarks and HtmlDiagramRenderer's remarks for why insertBefore silently blocks clicks
        // (it lands the overlay BEHIND the cluster's own background rect).
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("cluster.appendChild(a)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("insertBefore(a, cluster.firstChild", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OverlayScript_ComputesBandFromClusterAndLabelBBox()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("cluster.getBBox()", html, StringComparison.Ordinal);
        Assert.Contains("label.getBBox()", html, StringComparison.Ordinal);
        Assert.Contains(".cluster-label", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OverlayScript_UsesTransparentFill_NotNone()
    {
        // Same hit-testing hazard as the retired anchor node: fill:none is invisible to
        // pointer-events:visiblePainted hit-testing; the overlay rect must use a painted-but-
        // invisible transparent fill instead.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("'transparent'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OverlayScript_RunsAfterMermaidRenderResolves()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        int renderCall = html.IndexOf("await mermaid.render(", StringComparison.Ordinal);
        int overlayDefinition = html.IndexOf("function addTaskContainerOverlays", StringComparison.Ordinal);
        Assert.True(renderCall >= 0 && overlayDefinition >= 0, "both the render call and overlay function must be present");

        // The overlay must be INVOKED after the render call (the definition itself may appear
        // earlier in the script — only invocation order matters for correctness).
        int overlayInvocation = html.IndexOf("addTaskContainerOverlays(", renderCall, StringComparison.Ordinal);
        Assert.True(overlayInvocation > renderCall, "the overlay must be applied to the SVG mermaid.render just produced");
    }

    [Fact]
    public void Render_EmptyTaskFolderTargets_StillRendersValidPage()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);

        Assert.Contains("id=\"task-folder-targets\"", html, StringComparison.Ordinal);
        Assert.Contains("{}", html, StringComparison.Ordinal);
    }
}
