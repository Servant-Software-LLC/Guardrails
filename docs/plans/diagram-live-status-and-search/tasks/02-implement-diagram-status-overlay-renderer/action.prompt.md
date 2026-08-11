## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`02-implement-diagram-status-overlay-renderer`), NOT the stableId. The harness
  REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-implement-diagram-status-overlay-renderer": { "someKey": "someValue" } }`.
  This task does not need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Fill real logic over the stub(s) in `src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs`
that `01-author-tests-diagram-status-overlay-renderer` authored, so its tests pass. Do
NOT edit `tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs` — that file belongs to
the prior task. If you believe an authored test is genuinely wrong or incompatible with
a reasonable implementation, do NOT change it — write
`{"needsHuman": "<why the test seems wrong>"}` to the state-out path and stop instead.

Implement the `nodeStatuses` parameter's behavior exactly as described in the prior
task's tests: when `null`/omitted, output is byte-identical to the pre-existing
behavior; when provided, emit the `<meta http-equiv="refresh" content="2">` tag and a
corner-anchored status-badge overlay per entry (positioned via `getBBox()`/
`getBoundingClientRect()`, recomputed on `svg-pan-zoom` pan/zoom — mirror the existing
title-band overlay's post-render-script technique already in this file), with a
distinguishable spinner state for `"running"` and a settled glyph for every other
status value.
