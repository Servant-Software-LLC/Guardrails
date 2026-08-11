## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`01-author-tests-diagram-status-overlay-renderer`), NOT the stableId. The harness
  REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-diagram-status-overlay-renderer": { "someKey": "someValue" } }`.
  This task does not need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs` and
`src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs` (the stub file). After this task
completes, the harness runs a `git diff` check and rejects any edit outside these
paths — including changes to other production files, neighbouring test files, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you
hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Background

`src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs` currently exposes:

```csharp
public static string Render(
    string mermaidSource, string sourceHash, IReadOnlyDictionary<string, string> taskFolderTargets)
```

It returns the full `diagram.html` document — a Mermaid-rendered DAG with pan/zoom, a
`#legend` overlay, and a title-band click overlay per task container (all corner-anchored
`<div>`s positioned outside the Mermaid SVG, or embedded scripts that compute positions
from the SVG after Mermaid renders — see the existing `#legend` div and the
`addTaskContainerOverlays` function in the file for the exact technique to mirror).

This plan (issue #219) adds a LIVE STATUS OVERLAY: while a `guardrails run` is in
progress, `diagram.html` should show a small corner-anchored status badge on each task
container, preflight-check leaf, guardrail-check leaf, and the plan-level "Full Flight
Checks"/"Terminal Gate" brackets — an animated spinner while running, a settled icon
(check / X / "?") once finished — updated by re-rendering the file as the run
progresses (a separate task, `03-author-tests-diagram-observer`, builds the harness-side
piece that calls this rendering capability after each event; THIS task is scope to the
rendering capability itself, in isolation, driven directly with an in-memory status map).

### What to build (tests + stubs only — no real implementation)

1. **A new overload/parameters on `HtmlDiagramRenderer.Render`** that accept an OPTIONAL
   per-node status map AND an independent refresh-tag toggle, e.g.:

   ```csharp
   public static string Render(
       string mermaidSource, string sourceHash, IReadOnlyDictionary<string, string> taskFolderTargets,
       IReadOnlyDictionary<string, string>? nodeStatuses = null, bool includeRefresh = true)
   ```

   where `nodeStatuses` maps a Mermaid node/container id (the same ids already used
   throughout this file and `MermaidRenderer.cs` — task container ids, preflight/guardrail
   leaf ids, the plan-level bracket ids) to a status string: `"pending"`, `"running"`,
   `"passed"`, `"failed"`, or `"needs-human"`. Existing callers that omit both parameters
   (the static `graph` command, which never runs live) must be unaffected — `Render`
   with `nodeStatuses: null` (or omitted) must produce **byte-identical** output to
   today's `Render`, since the golden fixtures (`examples/hello-guardrails/...`,
   `docs/plans/08-parallel-execution/diagram.html`) are not part of this task's scope
   and must not need regenerating.

   **The refresh tag and the status overlay are INDEPENDENT** — mirror
   `src/Guardrails.Cli/Ui/LogSiteRenderer.cs`'s own `IndexHtml(..., includeRefresh: bool)`
   parameter shape, which the same during-run/final duality already uses for the log
   site: the DURING-RUN diagram (`nodeStatuses` non-null, `includeRefresh: true`, the
   default) carries the refresh tag; the FINAL settled diagram (`nodeStatuses` non-null
   — still showing each node's final settled status — but `includeRefresh: false`) must
   NOT carry it. A later task (`06-wire-diagramobserver-into-runcommand`) wires a final,
   no-refresh render at run-end, mirroring `RunCommand.cs`'s existing
   `LogSiteRenderer.ExportSite` end-of-run call for the log site — that wiring depends on
   this `includeRefresh` parameter existing.
2. **When `nodeStatuses` is provided**, the emitted HTML must additionally carry:
   - A per-node overlay badge `<div>` (or an embedded JS function that creates one per
     entry in `nodeStatuses`), positioned via `getBBox()`/`getBoundingClientRect()` on
     the corresponding SVG node, recomputed on every `svg-pan-zoom` pan/zoom event (the
     already-loaded `svg-pan-zoom` instance exposes `onPan`/`onZoom` callbacks — mirror
     how the existing title-band overlay code in this file already hooks post-render).
     A `"running"` status renders a spinner (a simple CSS `@keyframes` rotation is
     sufficient — do not add an external image/GIF dependency); a settled status renders
     a plain check/X/"?" glyph. This applies REGARDLESS of `includeRefresh`.
   - A `<meta http-equiv="refresh" content="2">` tag in `<head>`, if AND ONLY IF
     `nodeStatuses` is non-null AND `includeRefresh` is `true` (the default) — so a
     plain `file://` view re-reads itself with no server needed during a live run. When
     `nodeStatuses` is null/omitted (the static, one-shot `graph` command path), or when
     `includeRefresh` is explicitly `false` (the final settled render), this tag must be
     ABSENT.
3. **Write the MINIMAL stub** in `HtmlDiagramRenderer.cs`: the new parameters exist and
   compile, but the status-overlay logic can `throw new NotImplementedException()` (or
   return a placeholder) when `nodeStatuses is not null` — the test-author task's job is
   to make the TESTS compile and FAIL, not to implement the feature.
4. **Write the tests** in `HtmlDiagramRendererTests.cs` (xUnit — this repo's existing
   framework; mirror the existing test style in this same file: string-content
   assertions over the returned HTML, no browser driver). At minimum:
   - `Render_WithNullNodeStatuses_IsByteIdenticalToCurrentBehavior` — calling `Render`
     without the new parameters (or with `nodeStatuses: null`) produces the exact same
     string as before this change (paste a known-good fixture, or call the pre-existing
     overload shape and assert equality with the new call passing `nodeStatuses: null`).
   - `Render_WithNodeStatuses_EmitsAMetaRefreshTag` — passing a non-null `nodeStatuses`
     (default `includeRefresh: true`) causes `<meta http-equiv="refresh"` to appear;
     passing `null` does not.
   - `Render_WithNodeStatusesAndIncludeRefreshFalse_OmitsTheMetaRefreshTag` — passing a
     non-null `nodeStatuses` with `includeRefresh: false` still emits the status
     overlay/badges but does NOT emit `<meta http-equiv="refresh"`.
   - `Render_WithNodeStatuses_EmitsAnOverlayForEachStatusEntry` — for each key in a
     sample `nodeStatuses` map, the emitted HTML/script contains a reference to that
     node id in the overlay-rendering logic (a structural/string-content assertion —
     e.g. the node id appears inside the embedded status-map JSON or a generated JS
     data structure the overlay script reads).
   - `Render_WithRunningStatus_EmitsASpinnerClass_AndSettledStatusesEmitAGlyph` — the
     word "running" (or your chosen CSS class name) maps to a distinguishable
     spinner-shaped element/class in the output, distinct from a settled status's glyph.

   These tests must **compile** against the stub and **fail** when run (a legitimate
   `NotImplementedException`/assertion failure) — that failure IS the TDD red the
   `02-implement-diagram-status-overlay-renderer` task turns green.

### Note on browser-based verification (Level A gap)

No headless-browser driver (Playwright/Cypress) is configured in this repo, so these
tests are structural/string-content assertions on the generated HTML/JS — they prove
the right markup and data are EMITTED, not that a browser actually renders correct,
error-free positioning at runtime. This gap is intentional and documented in the
breakdown report; do not attempt to add a browser-driver dependency to close it here.

## Shared state

Your input state (a snapshot, read-only) is:

```json
{}
```

## Output contract

Write your new/changed state as a single JSON object fragment to this absolute path:

`C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\01-author-tests-diagram-status-overlay-renderer\attempt-1\action-out-fragment.json`

Write ONLY your own keys (conventionally namespaced under your task id). Do NOT modify state.json directly — the harness is the single writer and merges your fragment after guardrails pass. If you have nothing to contribute, write nothing.

If you cannot proceed without a human decision, write exactly `{ "needsHuman": "<your question>" }` to that same path and stop — the harness will escalate to a human without burning further retries.

## Worktree safety

You are running in an isolated git worktree dedicated to this task. `git stash` is **NOT safe** to use here: the stash stack (`refs/stash`) is repo-wide, not scoped to this worktree — a concurrent task (or a human's own diagnostic worktree) doing its own `git stash` around the same time can silently overwrite or steal yours, and a later `git stash pop` can apply the WRONG entry into this tree. Attempting to use `git stash` here will be blocked.

If you need to test against a clean baseline and restore your changes afterward, use this stash-free, entirely LOCAL alternative instead:

```
git diff > /tmp/mine.patch
git checkout -- <files>      # test the baseline
git apply /tmp/mine.patch    # restore your changes
```
