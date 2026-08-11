## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`06-wire-diagramobserver-into-runcommand`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "06-wire-diagramobserver-into-runcommand": { "someKey": "someValue" } }`. This task
  does not need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/RunCommand.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside this path — including
`tests/Guardrails.Integration.Tests/DiagramLiveStatusWiringTests.cs`, which the prior
task owns and which you must make pass WITHOUT editing it. If you believe that test is
genuinely wrong, do NOT change it — write `{"needsHuman": "<why>"}` to the state-out
path and stop instead.

### Background (authoring-time snapshot — verify before assuming it's still accurate)

This reflects the plan-authoring-time state of `RunCommand.cs`, before this plan's
earlier tasks ran — verify it's still accurate by reading the file yourself before
making changes; other tasks in this plan do not touch this file, but the codebase may
have moved since this prompt was written.

`RunCommand.cs`'s `RunAsync` method constructs and injects the analogous
`OnTheFlyLogSiteObserver` at exactly TWO call sites — grep for the literal text `new
OnTheFlyLogSiteObserver(` (do not rely on a line number, which may have moved): one in
the `if (live)` branch (wrapping a `LiveRunObserver`), one in the `else` branch
(wrapping a `ConsoleRunObserver`). Both results are then passed to `ExecuteAsync`.

Separately, near the end of `RunAsync`, grep for `LogSiteRenderer.ExportSite` — this is
the END-OF-RUN call that writes the FINAL, fully-static (no-refresh) log site after the
run settles, reading the freshly-persisted journal. This is the pattern to mirror for
the diagram's own final export.

### What to do

**1. Live wiring (during the run).** At BOTH `new OnTheFlyLogSiteObserver(` call sites,
additionally construct `OnTheFlyDiagramObserver`
(`src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`, already implemented) wrapping the
`OnTheFlyLogSiteObserver` instance the same way `OnTheFlyLogSiteObserver` itself wraps
`liveObserver`/`new ConsoleRunObserver(io.Out)` — i.e. chain the two decorators so BOTH
the log-site AND the diagram get kept live, and pass the OUTERMOST wrapped observer
(the diagram observer) to `ExecuteAsync` instead of the log-site observer directly.
Thread whatever `OnTheFlyDiagramObserver`'s constructor needs (the plan's Mermaid
source/hash/task-folder-targets, or however task 03/04 chose to source them — check
`OnTheFlyDiagramObserver.cs`'s actual constructor signature) from values already
available in `RunAsync` at that point (`probe.Plan`, `runId`, `probe.Plan.PlanDirectory`,
etc.) — do not invent new parameters to `RunAsync` itself; wire it entirely inside the
existing method body.

**2. Final export (at run-end).** Near the existing `LogSiteRenderer.ExportSite` call,
add an analogous final `diagram.html` write: re-read (or reuse, if still in scope) the
per-node status map at its FINAL settled state and call
`HtmlDiagramRenderer.Render(..., nodeStatuses: <final statuses>, includeRefresh: false)`,
overwriting `diagram.html` with a fully-static page carrying each node's settled status
but no refresh tag — the same during-run/final duality `ExportSite` already gives the
log site.

Make `05-author-tests-diagramobserver-wiring`'s test pass on BOTH assertions: after this
change, a real `guardrails run` must leave `diagram.html` showing settled (non-pending)
status for tasks that actually ran, AND the final file must carry no
`<meta http-equiv="refresh">` tag.
