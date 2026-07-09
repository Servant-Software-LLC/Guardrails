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
    public void Render_OverlayScript_SetsFillViaInlineStyle_NotTheFillAttribute()
    {
        // Real-browser regression (found live, not guessed): Mermaid's own generated stylesheet
        // carries a rule shaped like `#dag .cluster rect{fill:<theme color>;...}`. Since the
        // overlay rect is nested inside the .cluster group, that selector matches it too — and a
        // CSS class/tag selector beats a plain HTML presentation attribute, so
        // `rect.setAttribute('fill', 'transparent')` was silently overridden, painting a solid
        // theme-colored bar over the container's title text (confirmed via getComputedStyle:
        // attribute said "transparent", computed fill was the theme's cluster-background color).
        // An inline style (`rect.style.fill = ...`) wins over any external/embedded stylesheet
        // rule short of `!important`, so it reliably stays invisible. This test would have caught
        // the regression: `Assert.Contains("'transparent'")` alone passes for EITHER form.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("rect.style.fill = 'transparent'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rect.setAttribute('fill'", html, StringComparison.Ordinal);
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

    // === long task-name title wrap-overflow fix (issue cluster-label-wrap-overflow) ====
    //
    // Mermaid always wraps a cluster's title <div> to a hardcoded 200px width (bundled
    // mermaid@11.4.1's plain flowchart cluster shape never overrides createText's width=200
    // default), and dagre's layout pass reserves the container's header-strip height BEFORE that
    // wrapped height is known — so a long kebab-case task name that wraps to 2+ lines prints past
    // the reserved strip, overlapping the first leaf guardrail/preflight box below it. No Mermaid
    // config knob avoids this (verified against the bundled 11.4.1 source). The fix re-wraps the
    // SAME text at up to the container's own (already wider) width post-render instead, verified
    // headless against 8+ real plans (40+ long task names, container widths ~230px-~1100px,
    // wraps of 2-3 lines): every case converged to one line with the same healthy gap a
    // never-wrapped label already has, and every already-fits label was left byte-for-byte
    // unchanged (including the whole golden hello-guardrails example).

    [Fact]
    public void Render_DefinesFixWrappedClusterLabelsFunction()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("function fixWrappedClusterLabels(svgEl)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WrapFix_RunsAfterMermaidRenderResolves_AndBeforeTheOverlay()
    {
        // Ordering is load-bearing: addTaskContainerOverlays measures the label's CURRENT bbox to
        // size its click band, so a label the wrap fix already shrank back to one line must be
        // fixed FIRST, or the band would be sized for the original (possibly multi-line) wrap.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        int renderCall = html.IndexOf("await mermaid.render(", StringComparison.Ordinal);
        int wrapFixInvocation = html.IndexOf("fixWrappedClusterLabels(", renderCall, StringComparison.Ordinal);
        int overlayInvocation = html.IndexOf("addTaskContainerOverlays(", renderCall, StringComparison.Ordinal);

        Assert.True(renderCall >= 0 && wrapFixInvocation > renderCall && overlayInvocation > renderCall,
            "both post-render fixes must be invoked after the render call");
        Assert.True(wrapFixInvocation < overlayInvocation,
            "the wrap fix must run BEFORE the title-band overlay so the band reflects the corrected label size");
    }

    [Fact]
    public void Render_WrapFix_MeasuresViaScrollWidth_NotGetBoundingClientRect()
    {
        // Regression guard for the exact measurement pitfall this fix's own investigation hit:
        // getBoundingClientRect() reports on-screen pixels affected by the page's pan-zoom
        // transform, which is misleading when compared against a foreignObject's local width/
        // height attributes. scrollWidth/scrollHeight are local layout-box measurements, in the
        // SAME coordinate system as those attributes, regardless of the current zoom level.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        int fnStart = html.IndexOf("function fixWrappedClusterLabels", StringComparison.Ordinal);
        int fnEnd = html.IndexOf("\n}\n", fnStart, StringComparison.Ordinal);
        string fnBody = html[fnStart..fnEnd];

        Assert.Contains("div.scrollWidth", fnBody, StringComparison.Ordinal);
        Assert.Contains("div.scrollHeight", fnBody, StringComparison.Ordinal);
        Assert.DoesNotContain("getBoundingClientRect", fnBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WrapFix_NeverNarrowsBelowMermaidsOriginalWidth()
    {
        // The fix only ever WIDENS a label (fewer lines needs less height, never more) — it must
        // never choose a width narrower than what Mermaid already assigned, which would be a
        // regression risking MORE wrapping, not less.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("Math.max(currentWidth, clusterBox.width - 16)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WrapFix_SkipsLabelsThatAlreadyFitOnOneLine()
    {
        // Short names (e.g. every task in the golden hello-guardrails example) must be left
        // completely untouched — this is the guard that makes that a no-op.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("if (naturalWidth <= currentWidth) continue;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WrapFix_RevertsWhenWideningDoesNotReduceHeight()
    {
        // A container too narrow to help must fall back to Mermaid's original sizing rather than
        // risk an oddly-proportioned label — never regress relative to the pre-fix rendering.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("if (newHeight >= currentHeight)", html, StringComparison.Ordinal);
        Assert.Contains("div.setAttribute('style', savedInline);", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WrapFix_RecentersTheLabelOnTheSameMidpointAfterWidening()
    {
        // Mermaid centers the label group via translate(x - bbox.width / 2, y); widening without
        // re-centering would shift the (now wider) box off-center over the cluster.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("deltaCenter", html, StringComparison.Ordinal);
        Assert.Contains("label.setAttribute('transform',", html, StringComparison.Ordinal);
    }

    // === mid-edge direction arrowheads (issue #301) ===================================
    //
    // The DAG is drawn subgraph->subgraph (issue #210), so Mermaid clips each edge's own arrowhead
    // to the target cluster's outer border; on a long edge that routes PAST an unrelated sibling box,
    // that head is invisible along the crossing mid-section a reader's eye follows, so the edge reads
    // as directionless (or as a phantom dependency between the two boxes it passes between).
    // addEdgeDirectionMarkers injects a small filled arrowhead at each edge's geometric midpoint,
    // rotated to the path's local tangent so it points source->target — a second, always-visible
    // direction cue where the ambiguity actually is. Purely additive post-render SVG; it never alters
    // the Mermaid source, the DAG, the source-sha256, or diagram.md.

    [Fact]
    public void Render_DefinesEdgeDirectionMarkerFunction()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("function addEdgeDirectionMarkers(svgEl)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EdgeDirectionMarkers_InvokedAfterMermaidRenderResolves()
    {
        // The overlay must be applied to the SVG mermaid.render just produced (the definition may
        // appear earlier in the script — only invocation order matters for correctness).
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        int renderCall = html.IndexOf("await mermaid.render(", StringComparison.Ordinal);
        Assert.True(renderCall >= 0, "the render call must be present");
        int invocation = html.IndexOf("addEdgeDirectionMarkers(", renderCall, StringComparison.Ordinal);
        Assert.True(invocation > renderCall,
            "the direction markers must be applied AFTER the render call, to the SVG it produced");
    }

    [Fact]
    public void Render_EdgeDirectionMarkers_SelectEdgePaths_AndOrientAlongTheLocalTangent()
    {
        // The cue must (a) target the edge <path>s Mermaid emits and (b) orient by the path's own
        // geometry (midpoint + local tangent), not a fixed direction — that is what makes it point
        // source->target on an arbitrarily-routed crossing edge, independent of the clipped endpoint
        // arrowhead.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);
        string fnBody = EdgeDirectionFunctionBody(html);

        Assert.Contains("g.edgePaths", fnBody, StringComparison.Ordinal);
        Assert.Contains("getTotalLength()", fnBody, StringComparison.Ordinal);
        Assert.Contains("getPointAtLength(", fnBody, StringComparison.Ordinal);
        Assert.Contains("Math.atan2(", fnBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EdgeDirectionMarkers_DoNotInterceptClicks()
    {
        // The marker must never steal a click meant for a node, the container title-band overlay, or
        // a leaf source link — regression guard for the click affordances (#33/#235).
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);
        string fnBody = EdgeDirectionFunctionBody(html);

        Assert.Contains("marker.setAttribute('pointer-events', 'none')", fnBody, StringComparison.Ordinal);
    }

    private static string EdgeDirectionFunctionBody(string html)
    {
        int fnStart = html.IndexOf("function addEdgeDirectionMarkers", StringComparison.Ordinal);
        Assert.True(fnStart >= 0, "the direction-marker function must be present");
        int fnEnd = html.IndexOf("\n}\n", fnStart, StringComparison.Ordinal);
        Assert.True(fnEnd > fnStart, "the direction-marker function must be well-formed");
        return html[fnStart..fnEnd];
    }

    // === client-side search box (issue #220) ===========================================
    //
    // A fixed-position find box, overlaid the same way #bar/#legend already sit outside the Mermaid
    // SVG — purely client-side, no server round-trip, no new dependency (svg-pan-zoom is already
    // loaded). Typing substring-matches every node's id + visible label (task ids, preflight-check
    // names, guardrail-check names), highlights matches / dims the rest via pure class toggling on
    // the rendered SVG (instant — no Mermaid re-render), and pans the current match to the viewport
    // center. Being chrome (like the legend), it can never affect the embedded Mermaid source or its
    // source-sha256.

    [Fact]
    public void Render_IncludesASearchOverlay_WithInputCounterAndPrevNext()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("id=\"search\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"search-input\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"count\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"search-prev\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"search-next\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DefinesSetupSearchFunction()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        Assert.Contains("function setupSearch(svgEl, pz)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Search_InvokedAfterMermaidRenderResolves_WithTheSvgAndPanZoom()
    {
        // The search must be wired to the SVG mermaid.render just produced AND the live pan-zoom
        // instance (it pans to center a match), so it can only be invoked after both exist.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        int renderCall = html.IndexOf("await mermaid.render(", StringComparison.Ordinal);
        Assert.True(renderCall >= 0, "the render call must be present");
        int invocation = html.IndexOf("setupSearch(el, pz)", renderCall, StringComparison.Ordinal);
        Assert.True(invocation > renderCall, "search must be wired AFTER the render + pan-zoom init");
    }

    [Fact]
    public void Render_Search_MatchesNodeIdsAndLabels_AcrossContainersAndLeaves()
    {
        // The needle is the node id + its visible label text, gathered from BOTH task containers
        // (g.cluster) and leaf check nodes (g.node) — so a task number, a task name, a preflight
        // name, or a guardrail name all resolve.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);
        string fnBody = SearchFunctionBody(html);

        Assert.Contains("g.cluster[id], g.node[id]", fnBody, StringComparison.Ordinal);
        Assert.Contains("textContent", fnBody, StringComparison.Ordinal);
        Assert.Contains(".includes(q)", fnBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Search_HighlightsMatchesAndDimsTheRest()
    {
        // Highlight + dim is pure class toggling (no Mermaid re-render). The dim must skip an
        // element that CONTAINS a match, so a matched leaf inside a container is never dimmed by
        // dimming the container.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);
        string fnBody = SearchFunctionBody(html);

        Assert.Contains("gr-search-match", fnBody, StringComparison.Ordinal);
        Assert.Contains("gr-search-dim", fnBody, StringComparison.Ordinal);
        Assert.Contains(".contains(m)", fnBody, StringComparison.Ordinal);
        // The highlight/dim CSS classes must be defined in the stylesheet too.
        Assert.Contains(".gr-search-dim {", html, StringComparison.Ordinal);
        Assert.Contains(".gr-search-match >", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Search_PansTheCurrentMatchToCenter_ViaPanBy()
    {
        // Pans (leaving zoom to the user) using screen-space getBoundingClientRect math against the
        // live pan-zoom transform — so it stays correct at any current zoom/pan.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);
        string fnBody = SearchFunctionBody(html);

        Assert.Contains("getBoundingClientRect()", fnBody, StringComparison.Ordinal);
        Assert.Contains("pz.panBy(", fnBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Search_CyclesMatchesWithEnterAndShiftEnter()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);
        string fnBody = SearchFunctionBody(html);

        Assert.Contains("'Enter'", fnBody, StringComparison.Ordinal);
        Assert.Contains("e.shiftKey", fnBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Search_IsNotInsideTheEmbeddedGraphSourceScript()
    {
        // Like the legend, the search chrome must live OUTSIDE the raw-text <script id="graph-source">
        // element — it is not part of the Mermaid source, so it can never affect what parses that
        // source (rendering) or hashes it (GraphSourceHash / source-sha256).
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget);

        int scriptStart = html.IndexOf("id=\"graph-source\"", StringComparison.Ordinal);
        int scriptEnd = html.IndexOf("</script>", scriptStart, StringComparison.Ordinal);
        string scriptContent = html[scriptStart..scriptEnd];

        Assert.DoesNotContain("setupSearch", scriptContent, StringComparison.Ordinal);
        Assert.DoesNotContain("search-input", scriptContent, StringComparison.Ordinal);
    }

    private static string SearchFunctionBody(string html)
    {
        int fnStart = html.IndexOf("function setupSearch", StringComparison.Ordinal);
        Assert.True(fnStart >= 0, "the search function must be present");
        // setupSearch is a large function with nested helpers; bound the slice at the sentinel
        // "\nlet pz = null;" that immediately follows its definition in the template.
        int fnEnd = html.IndexOf("\nlet pz = null;", fnStart, StringComparison.Ordinal);
        Assert.True(fnEnd > fnStart, "the search function must be well-formed and precede the render block");
        return html[fnStart..fnEnd];
    }

    // === live status overlay (issue #219, SSOT §10.1) ==================================
    //
    // The 5-arg Render embeds a node-id -> status-token map as a third `<script id="node-status">`
    // blob (same verbatim/textContent treatment as the Mermaid source and the task-folder targets)
    // and appends inline-SVG badges over each node AFTER mermaid.render. `duringRun` toggles the meta
    // refresh + the spinner animation only. Status is pure chrome — it NEVER touches source-sha256.

    private static readonly IReadOnlyDictionary<string, string> SomeStatus =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["task_01_a"] = "running",
            ["task_01_a_gr_0"] = "passed",
            ["plan_guardrails"] = "needs-human",
        };

    [Fact]
    public void Render_5Arg_EmbedsNodeStatus_AsJsonBlob()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        Assert.Contains("id=\"node-status\"", html, StringComparison.Ordinal);
        Assert.Contains("\"task_01_a\"", html, StringComparison.Ordinal);
        Assert.Contains("\"running\"", html, StringComparison.Ordinal);
        Assert.Contains("\"passed\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DefinesBadgeFunction_AndInvokesItAfterMermaidRenderResolves()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        Assert.Contains("function addStatusBadges(svgEl)", html, StringComparison.Ordinal);

        int renderCall = html.IndexOf("await mermaid.render(", StringComparison.Ordinal);
        Assert.True(renderCall >= 0, "the render call must be present");
        int invocation = html.IndexOf("addStatusBadges(", renderCall, StringComparison.Ordinal);
        Assert.True(invocation > renderCall, "the status badges must be applied to the SVG mermaid.render just produced");
    }

    [Fact]
    public void Render_BadgeFunction_UsesInlineSvg_NoExternalImageUrl()
    {
        // Self-contained assets (file:// + strict-CSP safe): an animateTransform spinner + inline-SVG
        // paths, positioned from each node's getBBox() upper-right corner. No external image URL.
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        Assert.Contains("animateTransform", html, StringComparison.Ordinal);
        Assert.Contains("getBBox()", html, StringComparison.Ordinal);
        // A badge must never intercept a node / title-band / leaf-source click.
        Assert.Contains("'pointer-events', 'none'", html, StringComparison.Ordinal);
        // No external image reference for the badge assets.
        Assert.DoesNotContain(".gif", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DuringRunTrue_InjectsMetaRefresh_AndActiveSpinner()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        Assert.Contains("http-equiv=\"refresh\"", html, StringComparison.Ordinal);
        Assert.Contains("const GR_DURING_RUN = true;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DuringRunFalse_HasNoMetaRefresh_AndInactiveSpinner()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: false);

        Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.Ordinal);
        Assert.Contains("const GR_DURING_RUN = false;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_3ArgOverload_StillWorks_EmptyStatus_NoRefresh()
    {
        // The plan-root diagram.html (GraphCommand) path: the 3-arg overload delegates to the 5-arg
        // with an EMPTY status + duringRun:false — inert overlay scaffolding, no badges, no refresh.
        string html = HtmlDiagramRenderer.Render(Source, Hash, NoTargets);

        Assert.Contains("id=\"node-status\"", html, StringComparison.Ordinal); // scaffolding present...
        Assert.Contains(">{}</script>", html, StringComparison.Ordinal);       // ...but empty status
        Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.Ordinal);
        Assert.Contains("const GR_DURING_RUN = false;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_5Arg_NullStatus_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HtmlDiagramRenderer.Render(Source, Hash, NoTargets, null!, duringRun: false));
    }

    [Fact]
    public void Render_Status_IsHashNeutral_ProvenanceLineUnaffectedByStatus()
    {
        // Load-bearing: status is pure chrome. The provenance line (source-sha256) — and thus what
        // graph --check reads — must be byte-identical regardless of the status map (or duringRun).
        string firstNoStatus = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, NoTargets, duringRun: false)
            .Split('\n')[0];
        string firstWithStatus = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true)
            .Split('\n')[0];

        Assert.Equal($"<!-- guardrails:graph v1 source-sha256={Hash} -->", firstNoStatus);
        Assert.Equal(firstNoStatus, firstWithStatus);
    }

    [Fact]
    public void Render_Status_IsNotInsideTheEmbeddedGraphSourceScript()
    {
        // Like the legend/search chrome, the node-status blob + badge logic must live OUTSIDE the
        // raw-text <script id="graph-source"> element, so it can never reach what parses that source
        // (rendering) or hashes it (GraphSourceHash / source-sha256).
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        int scriptStart = html.IndexOf("id=\"graph-source\"", StringComparison.Ordinal);
        int scriptEnd = html.IndexOf("</script>", scriptStart, StringComparison.Ordinal);
        string scriptContent = html[scriptStart..scriptEnd];

        Assert.DoesNotContain("node-status", scriptContent, StringComparison.Ordinal);
        Assert.DoesNotContain("addStatusBadges", scriptContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_5Arg_IsDeterministic()
    {
        Assert.Equal(
            HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true),
            HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true));
    }
}
