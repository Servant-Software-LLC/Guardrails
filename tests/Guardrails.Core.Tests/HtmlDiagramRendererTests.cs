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
}
