# Live status overlay + search for diagram.html

Two related enhancements to `diagram.html` (the pan/zoom/fullscreen companion to `diagram.md`,
`src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs` + `MermaidRenderer.cs`), sequenced as two waves
in one plan since wave 2 is easiest to build once wave 1's per-node status classes exist.

## Wave 1 — live-updating status overlay (issue #219)

### Context

`diagram.html` is generated once, at breakdown/graph time, and never updates — so it can't show a
run in progress. The harness already solves the identical problem for the log site:
`src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs` is a decorator `IRunObserver` that wraps the
real observer, forwards every event, and after each one rewrites `logs/<runId>/index.html` from an
in-memory per-task status map — guarded by one `lock (_gate)` (events arrive from concurrent
worktree-mode workers), written atomically, best-effort (a render failure never flips a task's
outcome). The during-run page carries `<meta http-equiv="refresh" content="2">` so a plain
`file://` view re-reads itself with no server needed; the final/`--export` page drops the refresh.

`diagram.html` should get the same treatment. `IRunObserver` (`src/Guardrails.Core/Execution/
IRunObserver.cs`) already fires `TaskStarting`, `TaskFinished`, and — critically — per-check
`GuardrailFinished(TaskNode task, GuardrailResult result)`, so status can be tracked at the same
granularity `MermaidRenderer.cs` already renders: each task container, and each preflight-check /
guardrail-check LEAF inside it gets its own Mermaid node id (the container-model renderer landed
for the two-scope preflights feature, #183). The page already loads `svg-pan-zoom@3.6.1` and
`mermaid@11.4.1` from a CDN (`HtmlDiagramRenderer.cs`).

### The ask

1. A new decorator, e.g. `OnTheFlyDiagramObserver`, built the same way as
   `OnTheFlyLogSiteObserver`: wraps `IRunObserver`, forwards every event, and after
   `TaskStarting` / `TaskFinished` / `GuardrailFinished` updates a per-node status map (pending /
   running / passed / failed / needs-human) under one lock, then re-renders `diagram.html`
   atomically. Wire it into the same observer-composition call site `OnTheFlyLogSiteObserver` is
   wired into (find it and match the pattern — this is a composition-root wiring task: the new
   observer must actually be constructed and injected into the real observer chain, not just unit
   tested in isolation).
2. Extend `HtmlDiagramRenderer`/`MermaidRenderer`'s emitted HTML with a small per-node status-icon
   overlay: a corner-anchored badge per box (task container, preflight-check leaf, guardrail-check
   leaf, and the plan-level "Full Flight Checks" / "Terminal Gate" brackets) — an animated
   spinner GIF while running/in-flight, a settled icon (check / X / "?" for needs-human) once
   finished. Implement as absolutely-positioned overlay `<div>`s (the same technique the existing
   `#legend` overlay already uses to sit outside the Mermaid SVG), positioned from the
   corresponding SVG node's bounding box (`getBBox()`/`getBoundingClientRect()` after Mermaid
   renders) and recomputed on every pan/zoom event (the loaded `svg-pan-zoom` instance exposes
   pan/zoom callbacks) so badges track the viewport transform.
3. Reuse `<meta http-equiv="refresh">` for the during-run `file://` case (dropped once the run
   settles, matching the log site's pattern exactly).

### Acceptance

- A real `guardrails run` on a multi-task, parallel (worktree-mode) plan shows `diagram.html`
  transitioning nodes from pending to running to settled as the run proceeds, with no torn/garbled
  render observed under concurrent task completion.
- The final settled `diagram.html` (post-run, or `--export`) has NO refresh tag and is fully
  static — durable for later viewing, same duality as the log site.
- Status is shown at task, preflight-check, AND guardrail-check granularity, not just per-task.

## Wave 2 — search box with highlight + auto pan/zoom (issue #220)

### Context

On a large plan (20+ tasks), finding a specific named task/check by eye in `diagram.html` is slow.
Builds directly on wave 1: the same per-node ids wave 1's status map already tracks are exactly
what a search box matches against.

### The ask

A small fixed-position search input (same overlay technique as `#legend`/`#bar`/`#hint`/wave 1's
status badges), purely client-side:

1. Substring-match the typed text against every node's id and visible label (task ids, preflight-
   check names, guardrail-check names — already distinct, human-readable strings per
   `MermaidRenderer.cs`).
2. Matching node(s) get a bright outline/glow CSS class; non-matching nodes get a dimmed class —
   pure class toggling on the already-rendered SVG, no Mermaid re-render.
3. Auto pan/zoom to center the first match, using the already-loaded `svg-pan-zoom` instance's
   `pan()`/`zoom()`/`getPan()`/`getSizes()` API.
4. Multiple matches: an "N of M" counter with next/prev, mirroring a standard in-page find UX.

### Acceptance

- Typing a task-id fragment (e.g. `08`) or a guardrail-name fragment highlights and centers the
  matching node(s) on a plan with 20+ tasks, with no page reload.
- Composes with wave 1: searching for a task that is currently mid-run shows both the highlight
  AND its running status icon together.

## Stack

.NET 8 / xUnit v3 for the harness-side observer (wave 1's `OnTheFlyDiagramObserver` and its
composition-root wiring guardrail). Plain HTML/CSS/JS (no build step, no new dependency — the CDN
libs are already loaded) for the overlay rendering and search box, embedded the same way the
existing `#legend` overlay is. Verification: `dotnet test tests/Guardrails.Core.Tests` +
`tests/Guardrails.Cli.Tests` for the observer/wiring; a manual or scripted browser check for the
overlay/search behavior (no headless-browser driver is set up in this repo yet — see issue #219/
#220's own notes on the Level-A liveness-smoke gap if one becomes available).

## Related issues
#219 (wave 1), #220 (wave 2), #210 (separate cosmetic edge-routing fix, same file), #218 (the
four-folder container-model renderer both waves build on).
