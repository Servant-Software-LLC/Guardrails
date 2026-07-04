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
</style>
</head>
<body>
<div id="bar">
  <button id="fit">Fit</button>
  <button id="zin">+</button>
  <button id="zout">&minus;</button>
  <button id="fs">Fullscreen</button>
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
</div>
<div id="hint">scroll = zoom &middot; drag = pan &middot; Fit resets &middot; Fullscreen (or press F11).
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

// Inject a real clickable overlay on each task container's TITLE/LABEL ROW (issue #232/#233
// superseded, issue #235). Mermaid never fires a `click` directive on a subgraph/cluster element,
// and an anchor NODE inside the container gets packed into whatever sliver of space dagre's own
// layout leaves — useless for a content-dense container. The title band is different: Mermaid
// ALWAYS renders a cluster as exactly two children, a background <rect> then a <g
// class="cluster-label">, with the label in its own reserved header strip above any leaf content —
// so a full-width band from the cluster's top down to just past the label's bottom edge is empty BY
// CONSTRUCTION, regardless of how many guardrails/preflights the task has.
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

let pz = null;
try {
  const { svg } = await mermaid.render('dag', graph);
  document.getElementById('stage').innerHTML = svg;
  const el = document.querySelector('#stage svg');
  el.setAttribute('width', '100vw');
  el.setAttribute('height', '100vh');
  addTaskContainerOverlays(el);
  pz = svgPanZoom(el, { zoomEnabled: true, controlIconsEnabled: false, fit: true, center: true,
                        minZoom: 0.1, maxZoom: 20, zoomScaleSensitivity: 0.3 });
  window.addEventListener('resize', () => { pz.resize(); pz.fit(); pz.center(); });
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
