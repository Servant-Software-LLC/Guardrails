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
/// </remarks>
public static class HtmlDiagramRenderer
{
    /// <summary>
    /// Build the <c>diagram.html</c> document for <paramref name="mermaidSource"/> (the exact
    /// text rendered into <c>diagram.md</c>, including the <c>flowchart TD</c> header and the
    /// cosmetic <c>classDef</c> lines) stamped with <paramref name="sourceHash"/>.
    /// </summary>
    public static string Render(string mermaidSource, string sourceHash)
    {
        ArgumentNullException.ThrowIfNull(mermaidSource);
        ArgumentException.ThrowIfNullOrEmpty(sourceHash);

        // Newline-normalize so the file is byte-identical across OSes (mirrors the renderer/hash).
        string source = mermaidSource
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

        // Normalize the full output — the template raw-string literal picks up \r\n when the
        // file is checked out with CRLF on Windows; the mermaid source is already normalized above.
        return Template
            .Replace("__SOURCE_SHA256__", sourceHash, StringComparison.Ordinal)
            .Replace("__GRAPH_SOURCE__", source, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    // __SOURCE_SHA256__ is filled with a lowercase-hex hash (no escaping needed). __GRAPH_SOURCE__
    // is filled with the verbatim Mermaid text inside a raw-text <script> element (see remarks).
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
<script src="https://cdn.jsdelivr.net/npm/svg-pan-zoom@3.6.1/dist/svg-pan-zoom.min.js"></script>
<script type="module">
import mermaid from 'https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.esm.min.mjs';

const graph = document.getElementById('graph-source').textContent;
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

let pz = null;
try {
  const { svg } = await mermaid.render('dag', graph);
  document.getElementById('stage').innerHTML = svg;
  const el = document.querySelector('#stage svg');
  el.setAttribute('width', '100vw');
  el.setAttribute('height', '100vh');
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
