## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`07-author-tests-diagram-search-box`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "07-author-tests-diagram-search-box": { "someKey": "someValue" } }`. This task
  does not need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs` and
`src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs` (the stub). After this task
completes, the harness runs a `git diff` check and rejects any edit outside these
paths. An out-of-scope edit fails the task immediately and consumes a retry. If you hit
a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Background

This is issue #220, building on issue #219 (the live status overlay, already landed by
tasks 01-06 in this plan). Read `HtmlDiagramRenderer.cs`'s current state yourself before
starting — it now carries the status-overlay machinery this task's search box will sit
alongside (both are embedded client-side JS/CSS in the same generated `diagram.html`).

Add a small, fixed-position search input to the generated page — purely client-side,
NO harness-side observer or backend component needed (unlike issue #219's status
overlay, this is JS/CSS only, embedded directly in the `Render` template). Follow the
SAME overlay technique already used for `#legend`, `#bar`, `#hint`, and the status
badges: a fixed-position `<div>` outside the Mermaid SVG.

### What to build (tests + stubs only — no real implementation)

1. A search `<input>` element, always present in the generated HTML (unconditional —
   unlike the status overlay, search does not depend on `nodeStatuses` being non-null).
2. Client-side JS (embedded `<script>`) that, on input, substring-matches the typed text
   (case-insensitive) against every Mermaid node's id AND visible label (task ids,
   preflight-check names, guardrail-check names — grep this file's existing node-id/
   label generation to find where they are already available to JS, e.g. via an
   embedded JSON data structure similar to `__TASK_FOLDER_TARGETS__`).
3. Matching node(s) get a distinct CSS class (e.g. `.search-match`); non-matching nodes
   get a dimmed class (e.g. `.search-dim`) — pure class toggling, no Mermaid re-render.
4. Auto pan/zoom to center the FIRST match, using the already-loaded `svg-pan-zoom`
   instance's `pan()`/`zoom()`/`getPan()`/`getSizes()` API (mirror how the existing
   overlay-positioning code in this file already calls into that instance).
5. A "N of M" match counter with next/prev controls when there are multiple matches.
6. Write the MINIMAL stub: the search `<input>` and its wiring script exist and are
   emitted, but the actual match/highlight/pan-zoom logic can `throw new
   NotImplementedException()` (or be a visible TODO the tests catch) — this task's job
   is failing tests, not the real implementation.

### Tests to write

In `HtmlDiagramRendererTests.cs` (xUnit — this repo's framework; structural/string-
content assertions, no browser driver — no headless-browser driver is configured in
this repo, see the note below):

- `Render_AlwaysEmitsASearchInputElement` — the search `<input>` (or its container div)
  is present in EVERY render, regardless of `nodeStatuses`.
- `Render_SearchScript_ReferencesEveryNodeIdAndLabel` — the embedded search-matching
  data/script references the node ids and labels a sample Mermaid source produces (a
  structural assertion that the search data source exists and is populated).
- `Render_SearchScript_DefinesAHighlightAndDimClass` — the CSS/JS for the match/dim
  classes is present in the output.
- `Render_SearchScript_CallsThePanZoomApiForAutoCentering` — the embedded script
  references `pan(` / `zoom(` (or your chosen API calls) in the search-handling logic.

These must compile and FAIL against the throwing stub — that failure IS the TDD red
`08-implement-diagram-search-box` turns green.

### Note on browser-based verification (Level A gap)

No headless-browser driver (Playwright/Cypress) is configured in this repo, so these
tests are structural/string-content assertions — they prove the right markup/script is
emitted, not that a browser actually matches, highlights, and pans/zooms correctly at
runtime. This gap is intentional and documented in the breakdown report.
