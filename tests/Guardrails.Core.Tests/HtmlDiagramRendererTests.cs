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

    [Fact]
    public void Render_FirstLine_IsProvenanceComment_WithSameHash()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash);
        string firstLine = html.Split('\n')[0];

        // Same provenance shape as diagram.md, and FIRST so the \A-anchored reader (graph --check)
        // matches it in both files.
        Assert.Equal($"<!-- guardrails:graph v1 source-sha256={Hash} -->", firstLine);
    }

    [Fact]
    public void Render_EmbedsSourceVerbatim_InRawTextScript()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash);

        Assert.Contains("type=\"text/plain\"", html);
        Assert.Contains(Source, html); // verbatim — not interpolated into a JS string
        Assert.Contains("<!doctype html>", html);
    }

    [Fact]
    public void Render_EnablesClicksAndNavigation()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash);

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
        string html = HtmlDiagramRenderer.Render(Source, Hash);

        Assert.Contains("maxTextSize: 5000000", html);
        Assert.Contains("maxEdges: 100000", html);
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        Assert.Equal(HtmlDiagramRenderer.Render(Source, Hash), HtmlDiagramRenderer.Render(Source, Hash));
    }

    [Fact]
    public void Render_NormalizesCrlf_NoCarriageReturnsInOutput()
    {
        string html = HtmlDiagramRenderer.Render("flowchart TD\r\n  a-->b\r\n", Hash);
        Assert.DoesNotContain('\r', html);
    }

    [Fact]
    public void Render_EmptyHash_Throws()
    {
        Assert.Throws<ArgumentException>(() => HtmlDiagramRenderer.Render(Source, ""));
    }

    // === legend overlay (outside the Mermaid graph; see MermaidRenderer remarks) =======

    [Fact]
    public void Render_IncludesALegendOverlayDiv_OutsideTheEmbeddedMermaidSource()
    {
        // A Mermaid-native legend was prototyped and rendered broken headless (dagre treats a
        // disconnected subgraph as a phantom extra "task"); the only working placement is an HTML
        // overlay div outside the embedded Mermaid <script> source, mirroring #bar/#hint.
        string html = HtmlDiagramRenderer.Render(Source, Hash);

        Assert.Contains("id=\"legend\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_LegendOverlay_StatesBothAColorAndABeforeAfterTimingWord()
    {
        // A bare category name would not preserve the "preflights run before, guardrails run
        // after" semantic the removed nested boxes used to convey visually — the legend must spell
        // out both the colour mapping AND the timing/consequence in words.
        string html = HtmlDiagramRenderer.Render(Source, Hash);

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
        string html = HtmlDiagramRenderer.Render(Source, Hash);

        int scriptStart = html.IndexOf("id=\"graph-source\"", StringComparison.Ordinal);
        int scriptEnd = html.IndexOf("</script>", scriptStart, StringComparison.Ordinal);
        string scriptContent = html[scriptStart..scriptEnd];

        Assert.DoesNotContain("legend", scriptContent, StringComparison.Ordinal);
    }
}
