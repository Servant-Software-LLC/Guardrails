## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`05-author-tests-diagramobserver-wiring`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "05-author-tests-diagramobserver-wiring": { "someKey": "someValue" } }`. This task
  does not need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/DiagramLiveStatusWiringTests.cs` (a new file). After
this task completes, the harness runs a `git diff` check and rejects any edit outside
this path — including changes to `src/Guardrails.Cli/Commands/RunCommand.cs`, which the
NEXT task (`06-wire-diagramobserver-into-runcommand`) owns. An out-of-scope edit fails
the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol elsewhere, do NOT edit that file — write `{"needsHuman": "<what is
missing>"}` to the state-out path and stop.

### Background — this is the composition-root wiring test (issue #120 doctrine)

`OnTheFlyDiagramObserver` (implemented by `04-implement-diagram-observer`) exists and is
independently tested, but **nothing in `RunCommand.cs` constructs or injects it yet** —
so a real `guardrails run` today never actually calls it, and `diagram.html` never
updates live. This is exactly the composition-root gap issue #120 documents: component
tasks can all be green while the feature is inert because no task wires it into
production.

`RunCommand.cs` currently constructs and injects the analogous
`OnTheFlyLogSiteObserver` at exactly TWO call sites in its `RunAsync` method — grep for
the literal text `new OnTheFlyLogSiteObserver(` (it appears exactly twice: once in the
`if (live)` branch wrapping a `LiveRunObserver`, once in the `else` branch wrapping a
`ConsoleRunObserver`). Do NOT cite a line number for these — this reflects the
plan-authoring-time state; verify the exact current shape yourself by reading the file,
since other tasks in this plan may have touched nearby code by the time you run.

### What to write

A new test file `tests/Guardrails.Integration.Tests/DiagramLiveStatusWiringTests.cs`
(xUnit — this repo's existing framework) that drives the REAL `guardrails run` CLI
pipeline end-to-end (mirror the `RunCliCapturedAsync`/`StringConsoleIo` +
`RootCommand`/`RunCommand.Create(io)` pattern used throughout this test project — e.g.
`PlanPreflightPhaseTests.cs`, `PlanGuardrailPhaseTests.cs` — grep for
`StringConsoleIo` and `RunCommand.Create` for the exact call shape) against a small,
fast, real script-based plan (2-3 tasks is enough; a `ScriptPlanBuilder`-style fixture,
or hand-write one inline — grep this project for `ScriptPlanBuilder` for a ready-made
builder), run it with `--no-ui --no-log-server` so it completes headlessly, THEN read
the plan's generated `diagram.html` from disk and assert it reflects the run's SETTLED
outcome:

1. It does NOT contain `"pending"` for any task/check node that actually ran, or
   (stronger) it contains `"passed"` for at least one task/check id known to have
   succeeded.
2. It does NOT contain `<meta http-equiv="refresh"` — the FINAL, post-run `diagram.html`
   must be fully static (the same during-run/final duality
   `01-author-tests-diagram-status-overlay-renderer`'s `includeRefresh` parameter
   exists for; a later task wires a final, no-refresh render at run-end, mirroring
   `RunCommand.cs`'s existing `LogSiteRenderer.ExportSite` end-of-run call for the
   analogous log site).

This test must **compile and run today** (it references no new type — it only drives
the CLI and reads a file) and **FAIL** on both assertions, because nothing wires
`OnTheFlyDiagramObserver` (or a final export call) into `RunCommand.cs` yet, so
`diagram.html` is written ONCE (the static `graph`-style render, no status at all) and
never updated. That failure IS the TDD red `06-wire-diagramobserver-into-runcommand`
turns green — do NOT inject `OnTheFlyDiagramObserver` yourself into the test (that
would make it pass even unwired, which is exactly the false-green #120 warns against)
and do NOT construct `RunCommand`'s scheduler by hand with the observer pre-wired —
drive the real CLI `RunCommand.Create(io)` entry point.
