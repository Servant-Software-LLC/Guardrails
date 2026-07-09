using System.Text.Encodings.Web;
using System.Text.Json;

namespace Guardrails.Core.Graph;

/// <summary>
/// Renders the local-viewing companion to <c>diagram.md</c>: a self-contained
/// <c>diagram.html</c> that displays the SAME Mermaid DAG with pan / zoom / fullscreen, so a
/// large task+guardrail graph is actually navigable on disk (GitHub renders <c>diagram.md</c>
/// but offers no navigation). Pure: maps the rendered Mermaid text + its <c>source-sha256</c>
/// to an HTML string with no I/O (issue #33).
/// </summary>
/// <remarks>
/// <para>
/// The Mermaid source is embedded VERBATIM as the text content of a
/// <c>&lt;script type="text/plain"&gt;</c> element and read back via <c>textContent</c> — never
/// interpolated into a JS string. This sidesteps every escaping hazard (a label containing a
/// backtick, <c>${</c>, or a quote cannot break the page), and a <c>&lt;script&gt;</c> element's
/// content is "raw text" so entities are not decoded — the source round-trips byte-for-byte.
/// This is safe because <see cref="MermaidRenderer"/> HTML-escapes every label (so <c>&lt;</c>
/// becomes <c>&amp;lt;</c>), meaning the source can never contain <c>&lt;/script&gt;</c>.
/// </para>
/// <para>
/// The <c>source-sha256</c> provenance comment is emitted as the FIRST line (before
/// <c>&lt;!doctype html&gt;</c>) so the same <c>\A</c>-anchored reader that validates
/// <c>diagram.md</c> validates this file too (<c>graph --check</c>). A comment before the
/// doctype does not trigger quirks mode.
/// </para>
/// <para>
/// The renderer (Mermaid) and pan-zoom library load from a CDN, so the interactive view needs
/// internet the first time. GitHub will not execute the embedded script — this file is the
/// LOCAL artifact; <c>diagram.md</c> remains the portal artifact. Fully offline (inlined assets)
/// is a possible follow-up.
/// </para>
/// <para>
/// <b>Legend overlay.</b> A corner-anchored <c>#legend</c> <c>&lt;div&gt;</c>
/// (<c>position: fixed</c>), mirroring the existing <c>#bar</c>/<c>#hint</c> overlay divs, renders
/// <see cref="MermaidRenderer.LegendMarkdown"/>'s content OUTSIDE the Mermaid SVG. A Mermaid-native
/// legend (a disconnected subgraph of colour-swatch nodes) was prototyped and rendered BROKEN —
/// dagre lays out a disconnected subgraph as a phantom extra "task" overlapping the real DAG — so
/// the overlay div is the only approach that renders correctly. Static HTML, not templated from
/// the Mermaid source, so it carries no bearing on <c>source-sha256</c>.
/// </para>
/// <para>
/// <b>Task-container click target: a POST-RENDER SVG title-band overlay (issue #232/#233
/// superseded, issue #235).</b> A Mermaid <c>click</c> directive never fires on a subgraph/cluster
/// element (upstream limitation; see <see cref="MermaidRenderer"/> remarks), and the first fix
/// attempt — an invisible anchor NODE inside the container — proved useless in practice: dagre
/// sizes and packs it into whatever gap its own layout leaves, which for a content-dense container
/// is a tiny, off-center sliver (measured: 0.44% of the container's area on a real 4-guardrail
/// task). The fix implemented here instead runs AFTER <c>mermaid.render</c> resolves: for every
/// <c>g.cluster</c> element whose id starts with <c>task_</c>, compute a full-width band from the
/// cluster's own bounding box down to just past its <c>.cluster-label</c>'s bottom edge (Mermaid
/// always reserves this header strip above any leaf content, so it is empty BY CONSTRUCTION
/// regardless of how many guardrails/preflights the task has — unlike an interior anchor node) and
/// append a real <c>&lt;a href="..." target="_blank"&gt;&lt;rect fill="transparent"&gt;&lt;/a&gt;</c>
/// spanning it. APPENDED, not inserted first: a cluster's only two original children are its
/// background <c>&lt;rect&gt;</c> then its <c>.cluster-label</c> group, so appending puts the
/// overlay last in paint order (on top, clickable) without moving in front of the label text
/// visually (the rect is transparent) and — because every leaf check node lives in an entirely
/// separate top-level SVG group from its own container's <c>g.cluster</c> — the overlay can never
/// occlude or be occluded by a leaf node's own click target either way. The task→folder-path data
/// this script needs (<see cref="MermaidRenderer.TaskFolderTargets"/>) is embedded as a small JSON
/// object keyed by the SAME container id the Mermaid source uses, in a
/// <c>&lt;script type="application/json"&gt;</c> element read back via <c>textContent</c> (same
/// verbatim/never-interpolated treatment as the Mermaid source itself, immune to the same escaping
/// hazards).
/// </para>
/// <para>
/// <b>Long task-name title wrap-overflow, fixed by widening the label's foreignObject
/// post-render (issue cluster-label-wrap-overflow).</b> Mermaid always wraps a cluster's title
/// <c>&lt;div&gt;</c> to a hardcoded 200px width (the bundled <c>mermaid@11.4.1</c>'s plain
/// flowchart cluster shape never overrides <c>createText</c>'s <c>width = 200</c> default,
/// regardless of the container's own — usually much wider — computed width), and dagre's layout
/// pass reserves the container's header-strip height BEFORE that wrapped height is known, so a
/// long kebab-case task name (hyphens are valid soft line-break points) that wraps to 2+ lines
/// prints past the reserved strip, overlapping the first leaf guardrail/preflight box positioned
/// directly below. No Mermaid config knob avoids this — <c>flowchart.htmlLabels</c> only toggles
/// the html-vs-svg-text label renderer, and 11.4.1 has no documented option for cluster-label
/// wrap width. The fix (the <c>fixWrappedClusterLabels</c> function, run AFTER
/// <c>mermaid.render</c> resolves and BEFORE the title-band overlay above, since the overlay's
/// band height depends on the label's — by then possibly shrunk — bounding box) re-wraps the
/// SAME text at up to the container's own width instead: since the container is normally already
/// wider than 200px, this almost always drops the label to one line, which needs LESS height than
/// the original wrap — never more — so no other node/edge/container ever needs to move. Verified
/// headless against 8+ real plans (40+ long task names): every wrapped case converged to one line
/// with the same healthy gap a never-wrapped label already has, and every already-fits label
/// (including the whole golden <c>hello-guardrails</c> example) was left byte-for-byte unchanged.
/// </para>
/// <para>
/// <b>Mid-edge direction arrowheads (issue #301).</b> Because the DAG is drawn subgraph→subgraph
/// (issue #210), Mermaid clips an edge's own arrowhead to the TARGET cluster's OUTER border. On a
/// long edge that routes <i>past</i> an unrelated sibling box, that head lands far from — and is
/// invisible along — the crossing mid-section a reader's eye actually follows, so a reviewer sees an
/// apparently directionless connector in the gap between two boxes and either can't tell which way
/// the dependency runs or misreads it as a phantom dependency between the two boxes it merely passes
/// between (the DAG and the Mermaid source are correct — every edge is <c>--&gt;</c> — the failure is
/// purely rendering legibility). The <c>addEdgeDirectionMarkers</c> function (run AFTER
/// <c>mermaid.render</c> resolves, like the two fixes above) injects a small filled arrowhead at each
/// edge path's geometric MIDPOINT, rotated to the path's local tangent so it points source→target —
/// the direction cue lands exactly where the ambiguity is, independent of the clipped endpoint
/// marker, and reveals a crossing edge is passing THROUGH (not terminating) between the boxes. It is
/// purely additive post-render SVG (same pattern as the overlays): it never alters the Mermaid
/// source, the DAG, the <c>source-sha256</c>, or <c>diagram.md</c> — only the local viewer's
/// rendering. <c>diagram.md</c> (GitHub, no JS) instead relies on the
/// <see cref="MermaidRenderer.LegendMarkdown"/> "Edge direction" note that states the
/// dependency→dependent reading in words.
/// </para>
/// </remarks>
public static class HtmlDiagramRenderer
{
    /// <summary>
    /// Build the <c>diagram.html</c> document for <paramref name="mermaidSource"/> (the exact
    /// text rendered into <c>diagram.md</c>, including the <c>flowchart TD</c> header and the
    /// cosmetic <c>classDef</c> lines) stamped with <paramref name="sourceHash"/>. <paramref
    /// name="taskFolderTargets"/> is the task-container-id → plan-relative-folder-path map (see
    /// <see cref="MermaidRenderer.TaskFolderTargets"/>) the embedded title-band overlay script uses
    /// to resolve each container's click target (issue #235 — see class remarks).
    /// </summary>
    public static string Render(
        string mermaidSource, string sourceHash, IReadOnlyDictionary<string, string> taskFolderTargets)
    {
        ArgumentNullException.ThrowIfNull(mermaidSource);
        ArgumentException.ThrowIfNullOrEmpty(sourceHash);
        ArgumentNullException.ThrowIfNull(taskFolderTargets);

        // Newline-normalize so the file is byte-identical across OSes (mirrors the renderer/hash).
        string source = mermaidSource
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

        // Encoder that escapes NOTHING beyond what JSON strictly requires (default System.Text.Json
        // escapes '<', '>', '&', etc. as \uXXXX for HTML safety, which is unnecessary here — the
        // JSON sits inside a <script type="application/json"> element, read back via textContent,
        // exactly like the Mermaid source above — but using the unsafe-relaxed encoder keeps a
        // task-id containing e.g. "&" readable in the raw HTML source without changing behavior).
        string targetsJson = JsonSerializer.Serialize(taskFolderTargets, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        // Normalize the full output — the template raw-string literal picks up \r\n when the
        // file is checked out with CRLF on Windows; the mermaid source is already normalized above.
        return Template
            .Replace("__SOURCE_SHA256__", sourceHash, StringComparison.Ordinal)
            .Replace("__GRAPH_SOURCE__", source, StringComparison.Ordinal)
            .Replace("__TASK_FOLDER_TARGETS__", targetsJson, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    // __SOURCE_SHA256__ is filled with a lowercase-hex hash (no escaping needed). __GRAPH_SOURCE__
    // is filled with the verbatim Mermaid text inside a raw-text <script> element (see remarks).
    // __TASK_FOLDER_TARGETS__ is filled with a JSON object (container id -> folder path).
    private const string Template = """
<!-- guardrails:graph v1 source-sha256=__SOURCE_SHA256__ -->
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Guardrails — task/guardrail DAG</title>
<style>
  html, body { margin: 0; height: 100%; background: #0b0f14; color: #d6deeb;
               font-family: system-ui, sans-serif; overflow: hidden; }
  #bar { position: fixed; top: 8px; left: 8px; z-index: 10; display: flex; gap: 6px; }
  #bar button { background: #121a24; color: #d6deeb; border: 1px solid #243343;
                border-radius: 6px; padding: 6px 10px; cursor: pointer; font-size: 13px; }
  #bar button:hover { border-color: #7fdbff; }
  #stage { width: 100vw; height: 100vh; }
  #stage svg { width: 100vw; height: 100vh; max-width: none !important; }
  #hint { position: fixed; bottom: 8px; left: 8px; color: #5b6b7b; font-size: 12px;
          max-width: 90vw; }
  #legend { position: fixed; top: 8px; right: 8px; z-index: 10; background: #121a24;
            border: 1px solid #243343; border-radius: 6px; padding: 8px 12px;
            font-size: 12px; line-height: 1.5; max-width: 340px; }
  #legend .swatch { display: inline-block; width: 10px; height: 10px; border-radius: 2px;
                    margin-right: 6px; }
  /* Search overlay (issue #220): a fixed-position find box, mirroring #bar/#legend — purely
     client-side, no server round-trip, no new dependency. Sits top-center so it never overlaps
     the top-left #bar or top-right #legend. */
  #search { position: fixed; top: 8px; left: 50%; transform: translateX(-50%); z-index: 10;
            display: flex; gap: 6px; align-items: center; background: #121a24;
            border: 1px solid #243343; border-radius: 6px; padding: 4px 6px; }
  #search input { background: #0b0f14; color: #d6deeb; border: 1px solid #243343;
                  border-radius: 4px; padding: 5px 8px; font-size: 13px; width: 190px; }
  #search input:focus { outline: none; border-color: #7fdbff; }
  #search button { background: #121a24; color: #d6deeb; border: 1px solid #243343;
                   border-radius: 4px; padding: 5px 9px; cursor: pointer; font-size: 13px; }
  #search button:hover { border-color: #7fdbff; }
  #search button:disabled { opacity: 0.4; cursor: default; }
  #search #count { color: #7690a6; font-size: 12px; min-width: 52px; text-align: center;
                   font-variant-numeric: tabular-nums; }
  /* Highlight the matching node(s); dim the rest. Pure class toggling on the already-rendered SVG
     — no Mermaid re-render, so it is instant. A bright outline + glow on a match's own shape
     (rect for a box, the cluster's background rect for a container), and a lowered opacity on
     everything that neither matches nor CONTAINS a match (so a matched leaf inside a container is
     never dimmed by dimming its container). */
  .gr-search-dim { opacity: 0.18; }
  .gr-search-match > rect, .gr-search-match > polygon, .gr-search-match > path,
  .gr-search-match > .basic.label-container, .gr-search-match > circle {
    stroke: #ffd166 !important; stroke-width: 3px !important;
    filter: drop-shadow(0 0 5px #ffd166); }
  .gr-search-current > rect, .gr-search-current > polygon, .gr-search-current > path,
  .gr-search-current > .basic.label-container, .gr-search-current > circle {
    stroke: #ff9f1c !important; stroke-width: 4px !important;
    filter: drop-shadow(0 0 9px #ff9f1c); }
</style>
</head>
<body>
<div id="bar">
  <button id="fit">Fit</button>
  <button id="zin">+</button>
  <button id="zout">&minus;</button>
  <button id="fs">Fullscreen</button>
</div>
<div id="search">
  <input id="search-input" type="text" placeholder="Find task / check…"
         aria-label="Find a task, preflight, or guardrail node" autocomplete="off" spellcheck="false">
  <span id="count">0 / 0</span>
  <button id="search-prev" title="Previous match (Shift+Enter)" disabled>&lsaquo;</button>
  <button id="search-next" title="Next match (Enter)" disabled>&rsaquo;</button>
</div>
<div id="stage"></div>
<div id="legend">
  <div><span class="swatch" style="background:#e6d7ff;border:1px solid #6f42c1;"></span>
    <b>Preflight</b> &mdash; verified BEFORE the task's attempt loop; gates entry
    (dependency-delivery precondition)</div>
  <div><span class="swatch" style="background:#fff3cd;border:1px solid #b8860b;"></span>
    <b>Guardrail</b> &mdash; verified AFTER the task's action; must pass for the task to finish</div>
  <div><span class="swatch" style="background:#d4edda;border:1px solid #2e7d32;"></span>
    Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two
    checks once for the whole plan, at the very start and very end.</div>
  <div><span class="swatch" style="background:#7fdbff;border:1px solid #3aa0c2;"></span>
    <b>Edge direction</b> &mdash; edges run in execution order, from a dependency to its dependent
    (<code>A &rarr; B</code> = B dependsOn A). A cyan mid-edge arrow marks each edge's direction, so a
    long edge routing PAST an unrelated box is never a dependency on it &mdash; follow the arrow to
    its real target.</div>
</div>
<div id="hint">scroll = zoom &middot; drag = pan &middot; Fit resets &middot; Fullscreen (or press F11) &middot;
  search box (top) finds &amp; centers a node (Enter / Shift+Enter to cycle).
  Node clicks open source files &mdash; serve via a local HTTP server (e.g.
  <code>python -m http.server</code>) for clicks to work; browsers block
  <code>file://&rarr;file://</code> navigation by default.
  On GitHub, diagram.md renders instead.</div>

<script type="text/plain" id="graph-source">__GRAPH_SOURCE__</script>
<script type="application/json" id="task-folder-targets">__TASK_FOLDER_TARGETS__</script>
<script src="https://cdn.jsdelivr.net/npm/svg-pan-zoom@3.6.1/dist/svg-pan-zoom.min.js"></script>
<script type="module">
import mermaid from 'https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.esm.min.mjs';

const graph = document.getElementById('graph-source').textContent;
const taskFolderTargets = JSON.parse(document.getElementById('task-folder-targets').textContent);
// securityLevel 'loose' is required for the `click ... href` directives to open node sources;
// the content is the user's own local plan, served from file:// — not untrusted input.
// maxTextSize raises Mermaid's default 50 000-character source ceiling (issue #108): a large
// plan's DAG source easily exceeds it, and on overflow mermaid.render throws "Maximum text size
// in diagram exceeded" — landing every big plan in the catch-block "could not render" fallback.
// 5 000 000 covers DAGs far larger than any hand-reviewed plan while still bounding the parser.
// maxEdges raises Mermaid's default 500-edge ceiling for the same reason: a many-task plan fans
// out task→guardrail→done→dependent edges well past 500, and overflow throws "Maximum number of
// edges exceeded". Both ceilings must be lifted or a big plan still fails to render.
mermaid.initialize({ startOnLoad: false, theme: 'dark', securityLevel: 'loose', maxTextSize: 5000000, maxEdges: 100000, flowchart: { useMaxWidth: false } });

// Widen a wrapped cluster-title's foreignObject so a long kebab-case task name reflows onto
// FEWER lines instead of overflowing Mermaid's fixed header-strip reservation (issue
// cluster-label-wrap-overflow). Root cause, confirmed against the bundled mermaid@11.4.1 source
// (`src/rendering-util/createText.ts`'s `createText(el, text, { width = 200 })` default, which
// the plain flowchart "rect" cluster shape never overrides): a cluster's title <div> is always
// wrapped to a hardcoded 200px width regardless of the container's own — usually much wider —
// computed width, and dagre's LAYOUT PASS reserves header space for the container BEFORE that
// wrapped height is known, so it never grows to fit more than a couple of lines. A short label
// fits on one line inside 200px with room to spare (a healthy ~13px gap above the first leaf
// node, measured); a long name (hyphens are valid soft line-break points) wraps to 2 or more
// lines, and each line beyond the first prints a full line-height PAST what dagre reserved —
// landing on top of the first leaf guardrail/preflight box positioned directly below.
//
// Two Mermaid config knobs were checked and ruled out first: `flowchart.htmlLabels` only toggles
// the html-vs-svg-text label renderer (not label width), and 11.4.1 has no documented
// `flowchart`/theme-variable option for cluster-label wrapping width — 200 is a hardcoded
// parameter default the cluster-shape renderer never overrides, not a configurable value.
//
// The fix instead re-wraps the SAME text into fewer lines post-render: a task container is
// normally already wider than 200px (dagre sized it to fit its leaf content), so letting the
// identical label reflow at (up to) the container's own width almost always drops it to one
// line, which needs LESS height than Mermaid's original wrap — never more. That means this can
// only ever shrink the label's footprint, never grow it, so no other node/edge/container ever
// needs to move to make room. Verified headless against 8+ real plans (40+ long task names,
// container widths from ~230px to ~1100px, wraps from 2 to 3 lines): every case converged to one
// line with the same healthy negative gap a short, never-wrapped label already has; every
// already-fits-on-one-line label (including every task in the golden `hello-guardrails` example)
// was left byte-for-byte unchanged.
function fixWrappedClusterLabels(svgEl) {
  const clusters = svgEl.querySelectorAll('g.cluster');
  for (const cluster of clusters) {
    const label = cluster.querySelector('.cluster-label');
    if (!label) continue;
    const fo = label.querySelector('foreignObject');
    const div = fo ? fo.querySelector('div') : null;
    if (!fo || !div) continue;

    const currentWidth = parseFloat(fo.getAttribute('width')) || 0;
    const currentHeight = parseFloat(fo.getAttribute('height')) || 0;
    if (currentWidth <= 0 || currentHeight <= 0) continue;

    // scrollWidth/scrollHeight are LOCAL layout-box measurements in the foreignObject's own
    // SVG-user-unit coordinate system — deliberately NOT the screen-space rect API (see class
    // remarks): a screen-space read is affected by every ancestor SVG transform (dagre's per-node
    // translate, the pan-zoom viewport's zoom/pan matrix), so it would be misleading compared
    // against the foreignObject's own width/height attributes. Measure the label's natural
    // single-line width by temporarily lifting the wrap constraint Mermaid applied, then restore
    // it immediately.
    const savedInline = div.getAttribute('style');
    div.style.whiteSpace = 'nowrap';
    div.style.width = 'auto';
    div.style.maxWidth = 'none';
    // +2px safety buffer: scrollWidth can round down a hair short of the true subpixel width
    // needed for one line (observed empirically — re-applying the exact measured value as a
    // fixed width sometimes still wrapped to 2 lines by a fraction of a pixel).
    const naturalWidth = div.scrollWidth + 2;
    div.setAttribute('style', savedInline); // restore Mermaid's original wrapped styling

    if (naturalWidth <= currentWidth) continue; // already fits on one line — untouched

    // Never widen past the cluster's own body (leave a small margin) — the container's leaf
    // content already determined this width, so the label can use up to it without ever moving
    // a leaf node, edge, or sibling container. Never NARROWER than Mermaid's original width either
    // — this only ever grows the box.
    const clusterBox = cluster.getBBox();
    const newWidth = Math.min(naturalWidth, Math.max(currentWidth, clusterBox.width - 16));

    div.style.whiteSpace = 'break-spaces';
    div.style.display = 'table';
    div.style.width = newWidth + 'px';
    div.style.maxWidth = newWidth + 'px';
    const newHeight = div.scrollHeight;

    if (newHeight >= currentHeight) {
      // Widening did not actually reduce the wrapped height (the cluster is too narrow to fit
      // even one more character per line) — revert rather than risk an oddly-proportioned label.
      div.setAttribute('style', savedInline);
      continue;
    }

    fo.setAttribute('width', String(newWidth));
    fo.setAttribute('height', String(newHeight));

    // Mermaid centers the label group on the cluster's horizontal midpoint via
    // `translate(x - bbox.width / 2, y)`; widening the box without re-centering would shift it
    // left of center, so shift the existing translate by half the width delta to keep it on the
    // SAME midpoint. The label's y stays fixed — only its height shrank, so its bottom edge
    // simply moves UP, further from the first leaf node, never displacing it.
    const transform = label.getAttribute('transform') || '';
    const match = transform.match(/translate\(([-\d.eE]+)\s*,\s*([-\d.eE]+)\)/);
    if (match) {
      const oldX = parseFloat(match[1]);
      const deltaCenter = (newWidth - currentWidth) / 2;
      label.setAttribute('transform', `translate(${oldX - deltaCenter}, ${match[2]})`);
    }
  }
}

// Inject a real clickable overlay on each task container's TITLE/LABEL ROW (issue #232/#233
// superseded, issue #235). Mermaid never fires a `click` directive on a subgraph/cluster element,
// and an anchor NODE inside the container gets packed into whatever sliver of space dagre's own
// layout leaves — useless for a content-dense container. The title band is different: Mermaid
// ALWAYS renders a cluster as exactly two children, a background <rect> then a <g
// class="cluster-label">, with the label in its own reserved header strip above any leaf content —
// so a full-width band from the cluster's top down to just past the label's bottom edge is empty BY
// CONSTRUCTION, regardless of how many guardrails/preflights the task has. Must run AFTER
// fixWrappedClusterLabels (above): the band height is measured from the label's CURRENT bbox, so
// a label that fixWrappedClusterLabels already shrank back to one line gets a correspondingly
// short band, not one sized for its original (possibly multi-line) wrap.
function addTaskContainerOverlays(svgEl) {
  const ns = 'http://www.w3.org/2000/svg';
  const clusters = svgEl.querySelectorAll('g.cluster');
  for (const cluster of clusters) {
    const target = taskFolderTargets[cluster.id];
    if (!target) continue; // not a task container (e.g. plan_preflights/plan_guardrails), or no map entry
    const label = cluster.querySelector('.cluster-label');
    if (!label) continue;

    const clusterBox = cluster.getBBox();
    const labelBox = label.getBBox();
    const bandHeight = (labelBox.y + labelBox.height) - clusterBox.y + 8; // small padding below the title

    const a = document.createElementNS(ns, 'a');
    a.setAttribute('href', target);
    a.setAttribute('target', '_blank');
    a.setAttribute('aria-label', 'Open task folder: ' + target);

    const rect = document.createElementNS(ns, 'rect');
    rect.setAttribute('x', String(clusterBox.x));
    rect.setAttribute('y', String(clusterBox.y));
    rect.setAttribute('width', String(clusterBox.width));
    rect.setAttribute('height', String(bandHeight));
    // Inline style (not the `fill` ATTRIBUTE) deliberately: Mermaid's own generated stylesheet
    // carries a rule like `#dag .cluster rect{fill:<theme color>;...}` that colors every cluster's
    // background rect. Since this overlay rect is nested inside the .cluster group (so it stays in
    // the same paint context as the background rect it sits atop), that same selector matches it
    // too — and a CSS class/tag selector beats a plain presentation attribute, so `fill="transparent"`
    // as an attribute got silently overridden, painting a solid theme-colored bar over the title text.
    // An inline style wins over any external/embedded stylesheet rule (short of `!important`), so it
    // reliably stays invisible regardless of Mermaid's theme.
    rect.style.fill = 'transparent';
    rect.style.cursor = 'pointer';

    a.appendChild(rect);
    // Append as the LAST child — do NOT prepend as the first child. A cluster's only two original
    // children are its background <rect> then its .cluster-label group (in that paint order).
    // Appending puts the overlay on top of the background rect (so it is actually clickable)
    // without covering the label text visually (the rect is transparent either way) — prepending
    // instead would put the overlay BEHIND the background rect (which becomes second-in-order and
    // paints over it), silently blocking every click.
    cluster.appendChild(a);
  }
}

// Draw a mid-edge direction arrowhead on every DAG edge (issue #301). The DAG is drawn
// subgraph->subgraph (issue #210), so Mermaid clips an edge's own arrowhead to the TARGET cluster's
// OUTER border; on a long edge that routes PAST an unrelated sibling box, that head is far from — and
// invisible along — the crossing mid-section a reader's eye follows, so the connector reads as
// directionless (or as a phantom dependency between the two boxes it passes between). The endpoint
// arrowhead is correct but unreadable there; this adds a second, always-visible cue where the
// ambiguity actually is. Purely additive post-render SVG — never touches the Mermaid source, the DAG,
// the source-sha256, or diagram.md.
const EDGE_ARROW_COLOR = '#7fdbff'; // keep in sync with the #legend "Edge direction" swatch
function addEdgeDirectionMarkers(svgEl) {
  const ns = 'http://www.w3.org/2000/svg';
  // Mermaid draws every flowchart edge as a <path class="flowchart-link"> inside <g class="edgePaths">.
  // Select via both the group and the class so a future class-name shift still resolves the edges.
  const paths = svgEl.querySelectorAll('g.edgePaths path, path.flowchart-link');
  const seen = new Set();
  for (const path of paths) {
    if (seen.has(path)) continue;
    seen.add(path);
    let len = 0;
    try { len = path.getTotalLength(); } catch (e) { continue; }
    if (!(len > 0)) continue;

    // The midpoint is where a reader's eye lands on a long crossing edge; a short chord around it
    // gives the local tangent. dagre builds each path's `d` from source to target, so INCREASING arc
    // length is the dependency direction — no need to know the endpoints' coordinates.
    const step = Math.min(4, len / 2);
    const mid = path.getPointAtLength(len / 2);
    const back = path.getPointAtLength(len / 2 - step);
    const fwd = path.getPointAtLength(len / 2 + step);
    const angle = Math.atan2(fwd.y - back.y, fwd.x - back.x) * 180 / Math.PI;

    const marker = document.createElementNS(ns, 'path');
    marker.setAttribute('d', 'M -5 -6 L 8 0 L -5 6 Z'); // triangle pointing +x before rotation
    marker.setAttribute('transform', 'translate(' + mid.x + ', ' + mid.y + ') rotate(' + angle + ')');
    marker.setAttribute('class', 'gr-edge-dir');
    marker.style.fill = EDGE_ARROW_COLOR;
    marker.style.stroke = 'none';
    // Never intercept a click meant for a node, the container title-band overlay, or a leaf source link.
    marker.setAttribute('pointer-events', 'none');

    // Append inside the edge's own group so it paints above the edge line but below the node boxes
    // (Mermaid paints edgePaths before nodes) — a mid-edge point is open space, so it never covers a box.
    (path.parentNode || svgEl).appendChild(marker);
  }
}

// Client-side find box (issue #220). Substring-match the typed text against every searchable
// node's id AND its visible label — task containers (task_<base>, plan_preflights, plan_guardrails)
// and every leaf preflight/guardrail check node — highlighting matches, dimming the rest, and
// panning the current match to the viewport center. Pure post-render DOM work on the SVG
// mermaid.render produced: no Mermaid re-render (so it is instant), no new dependency (svg-pan-zoom
// is already loaded), and no bearing on the embedded Mermaid source or its source-sha256 — this is
// chrome, exactly like #bar/#legend. Panning uses getBoundingClientRect() screen-space math against
// the live svg-pan-zoom transform, so it stays correct at any current zoom/pan without
// reconstructing the SVG coordinate transform by hand.
function setupSearch(svgEl, pz) {
  const input = document.getElementById('search-input');
  const countEl = document.getElementById('count');
  const prevBtn = document.getElementById('search-prev');
  const nextBtn = document.getElementById('search-next');
  if (!input) return;

  // Collect the searchable elements once: task containers (g.cluster with an id) and leaf check
  // nodes (g.node with an id). Each carries a lowercased needle = id + ' ' + visible label text,
  // so typing a task number (matches the container id `task_08_…`), a task name, a preflight name,
  // or a guardrail name all resolve — every one is a distinct human-readable string in the SVG.
  const items = [];
  for (const g of svgEl.querySelectorAll('g.cluster[id], g.node[id]')) {
    const label = (g.textContent || '').replace(/\s+/g, ' ').trim();
    items.push({ el: g, needle: (g.id + ' ' + label).toLowerCase() });
  }

  let matches = [];
  let current = -1;

  function clearClasses() {
    for (const it of items) {
      it.el.classList.remove('gr-search-dim', 'gr-search-match', 'gr-search-current');
    }
  }

  // Pan (leaving zoom under the user's control) so the matched node's center lands at the viewport
  // center. getBoundingClientRect already reflects the current pan-zoom transform, so the screen-
  // space delta is exact — the same "jump to it" a browser's Ctrl+F gives, in the diagram's view.
  function centerOn(el) {
    if (!pz) return;
    const svgRect = svgEl.getBoundingClientRect();
    const nodeRect = el.getBoundingClientRect();
    if (!nodeRect.width && !nodeRect.height) return;
    const dx = (svgRect.width / 2) - (nodeRect.left + nodeRect.width / 2 - svgRect.left);
    const dy = (svgRect.height / 2) - (nodeRect.top + nodeRect.height / 2 - svgRect.top);
    pz.panBy({ x: dx, y: dy });
  }

  function setCurrent(i) {
    if (!matches.length) return;
    if (current >= 0 && current < matches.length) matches[current].classList.remove('gr-search-current');
    current = ((i % matches.length) + matches.length) % matches.length; // wrap both directions
    matches[current].classList.add('gr-search-current');
    countEl.textContent = (current + 1) + ' / ' + matches.length;
    centerOn(matches[current]);
  }

  function apply() {
    const q = input.value.replace(/\s+/g, ' ').trim().toLowerCase();
    clearClasses();
    current = -1;
    if (!q) {
      matches = [];
      countEl.textContent = '0 / 0';
      prevBtn.disabled = nextBtn.disabled = true;
      return;
    }
    const matchSet = new Set();
    matches = [];
    for (const it of items) {
      if (it.needle.includes(q)) { matchSet.add(it.el); matches.push(it.el); }
    }
    // Dim everything that neither matches nor CONTAINS a match, so a matched leaf inside an
    // unmatched task container is never dimmed by dimming its container.
    for (const it of items) {
      if (matchSet.has(it.el)) { it.el.classList.add('gr-search-match'); continue; }
      let containsMatch = false;
      for (const m of matches) { if (it.el !== m && it.el.contains(m)) { containsMatch = true; break; } }
      if (!containsMatch) it.el.classList.add('gr-search-dim');
    }
    prevBtn.disabled = nextBtn.disabled = matches.length === 0;
    if (matches.length) { setCurrent(0); } else { countEl.textContent = '0 / 0'; }
  }

  input.addEventListener('input', apply);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); if (matches.length) setCurrent(current + (e.shiftKey ? -1 : 1)); }
  });
  prevBtn.addEventListener('click', () => { if (matches.length) setCurrent(current - 1); });
  nextBtn.addEventListener('click', () => { if (matches.length) setCurrent(current + 1); });
}

let pz = null;
try {
  const { svg } = await mermaid.render('dag', graph);
  document.getElementById('stage').innerHTML = svg;
  const el = document.querySelector('#stage svg');
  el.setAttribute('width', '100vw');
  el.setAttribute('height', '100vh');
  fixWrappedClusterLabels(el);
  addTaskContainerOverlays(el);
  addEdgeDirectionMarkers(el);
  pz = svgPanZoom(el, { zoomEnabled: true, controlIconsEnabled: false, fit: true, center: true,
                        minZoom: 0.1, maxZoom: 20, zoomScaleSensitivity: 0.3 });
  window.addEventListener('resize', () => { pz.resize(); pz.fit(); pz.center(); });
  setupSearch(el, pz); // wire the find box to the rendered SVG + pan-zoom instance (issue #220)
} catch (e) {
  // Surface the actual failure (offline CDN, or a Mermaid limit we have not yet lifted) instead of
  // guessing — a generic "offline?" message sent every big-plan #108 failure down the wrong path.
  const detail = (e && e.message) ? String(e.message) : String(e);
  const p = document.createElement('p');
  p.style.padding = '1rem';
  p.textContent = 'Could not render the diagram: ' + detail +
    ' — the DAG source is embedded in this page, and diagram.md renders on GitHub.';
  const stage = document.getElementById('stage');
  stage.innerHTML = '';
  stage.appendChild(p);
}

document.getElementById('fit').onclick  = () => { if (pz) { pz.fit(); pz.center(); } };
document.getElementById('zin').onclick  = () => { if (pz) pz.zoomBy(1.3); };
document.getElementById('zout').onclick = () => { if (pz) pz.zoomBy(0.77); };
document.getElementById('fs').onclick   = () => document.documentElement.requestFullscreen();
</script>
</body>
</html>
""";
}
