## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`04-implement-diagram-observer`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04-implement-diagram-observer": { "someKey": "someValue" } }`. This task does not
  need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Fill real logic over the stub in `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` that
`03-author-tests-diagram-observer` authored, so its tests pass. Do NOT edit
`tests/Guardrails.Integration.Tests/OnTheFlyDiagramObserverTests.cs` — that file belongs
to the prior task. If you believe an authored test is genuinely wrong or incompatible
with a reasonable implementation, do NOT change it — write `{"needsHuman": "<why the
test seems wrong>"}` to the state-out path and stop instead.

Implement the render call exactly as the prior task's tests expect: on `TaskStarting`,
`GuardrailFinished`, and `TaskFinished`, update the in-memory per-node status map under
the lock, forward the event to `_inner`, then call `HtmlDiagramRenderer.Render(...,
nodeStatuses: <the current map>)` and write the result atomically to `diagram.html`
(mirror `OnTheFlyLogSiteObserver`'s `TryRender` best-effort wrapper — a render/write
failure must be swallowed, never thrown, never flip a task's outcome). Every other
`IRunObserver` member forwards to `_inner` unchanged.
