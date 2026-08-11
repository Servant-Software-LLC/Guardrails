## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`08-implement-diagram-search-box`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "08-implement-diagram-search-box": { "someKey": "someValue" } }`. This task does
  not need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Fill real logic over the stub(s) in `src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs`
that `07-author-tests-diagram-search-box` authored, so its tests pass. Do NOT edit
`tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs` — that file belongs to the
prior task. If you believe an authored test is genuinely wrong or incompatible with a
reasonable implementation, do NOT change it — write `{"needsHuman": "<why the test
seems wrong>"}` to the state-out path and stop instead.

Implement the search box exactly as the prior task's tests expect: substring-match
(case-insensitive) against every node's id and label, toggle highlight/dim CSS classes,
auto pan/zoom to the first match via the `svg-pan-zoom` API, and an "N of M" counter
with next/prev when there are multiple matches.
