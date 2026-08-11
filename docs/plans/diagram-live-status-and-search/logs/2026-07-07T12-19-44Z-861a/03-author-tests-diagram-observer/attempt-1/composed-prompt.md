## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in
  (`03-author-tests-diagram-observer`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "03-author-tests-diagram-observer": { "someKey": "someValue" } }`. This task does
  not need to publish any state — it is fine to write no fragment at all.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/OnTheFlyDiagramObserverTests.cs` and
`src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` (the new stub file). After this task
completes, the harness runs a `git diff` check and rejects any edit outside these
paths. An out-of-scope edit fails the task immediately and consumes a retry. If you hit
a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Background

`src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs` is an existing, working decorator
`IRunObserver` — read it in full before starting; the new class mirrors its exact shape.
It wraps a real observer (`_inner`), forwards every `IRunObserver` event to it, and after
forwarding certain events re-renders a live artifact from an in-memory per-task status
map, serialized under one `lock (_gate)` object, with renders wrapped best-effort (a
render failure never flips a task's outcome — see its private `TryRender` helper).

`IRunObserver` (`src/Guardrails.Core/Execution/IRunObserver.cs`) exposes: `TaskStarting`,
`AttemptStarting`, `TaskFinished`, `GuardrailFinished`, `PlanHashMismatch`,
`ParallelismClampedNoProvider`, `CleanupFailed`, `PromptPaused`.

`02-implement-diagram-status-overlay-renderer` (already landed) added a `nodeStatuses`
parameter to `HtmlDiagramRenderer.Render(mermaidSource, sourceHash, taskFolderTargets,
nodeStatuses)` — when non-null, it emits the live status overlay + refresh meta tag this
new observer will call into.

### What to build (tests + stubs only — no real implementation)

Create `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`, a new `sealed class
OnTheFlyDiagramObserver : IRunObserver` in namespace `Guardrails.Cli.Ui`, mirroring
`OnTheFlyLogSiteObserver`'s shape exactly:

- Constructor takes (at minimum) the inner `IRunObserver` to wrap, the plan's
  `IReadOnlyList<TaskNode> tasks`, the path to write `diagram.html` to, and whatever
  Mermaid-source/hash/task-folder-targets `HtmlDiagramRenderer.Render` needs to
  re-render (thread these through however is cleanest — you may need to read the
  ALREADY-WRITTEN `diagram.html`'s embedded `<script type="text/plain"
  id="graph-source">` / `<script type="application/json" id="task-folder-targets">`
  content back out to get the Mermaid source + targets map for re-rendering, since the
  observer does not have direct access to the `PlanDefinition` the CLI's `graph` command
  used to generate it originally — OR thread those two values in via the constructor;
  pick whichever is simpler and state your choice).
- On `TaskStarting`: flip that task's node status to `"running"` (and its preflight/
  guardrail leaf ids, if you track per-check granularity at this stage — the coarser
  per-task-only version is acceptable for THIS task; per-check granularity can also be
  driven by `GuardrailFinished`, see below), forward to `_inner`, then re-render
  `diagram.html` under the lock (best-effort — swallow a render failure).
- On `GuardrailFinished(TaskNode task, GuardrailResult result)`: flip that specific
  check's node id to `"passed"` or `"failed"` per `result`, forward, re-render.
- On `TaskFinished(TaskResult result)`: flip the task's node status to its settled
  outcome (`"passed"` / `"failed"` / `"needs-human"`, mapped from `result.Outcome`),
  forward, re-render.
- Every other `IRunObserver` member: forward to `_inner` unchanged (no status-map
  involvement) — mirror `OnTheFlyLogSiteObserver`'s pass-through members exactly.
- Write the MINIMAL stub: the class and constructor compile with the right shape, but
  the render call can `throw new NotImplementedException()` — this task's job is
  failing tests, not the real implementation.

### Tests to write

In `tests/Guardrails.Integration.Tests/OnTheFlyDiagramObserverTests.cs` (xUnit — this
repo's existing framework), construct a real `OnTheFlyDiagramObserver` wrapping a fake/
no-op `IRunObserver`, fire `TaskStarting`/`GuardrailFinished`/`TaskFinished` against it
directly (no real Scheduler needed — this is a focused unit test of the decorator
itself), and assert on the resulting `diagram.html` FILE CONTENTS written to a temp
directory:

- `TaskStarting_RerendersDiagramWithTheTaskMarkedRunning` — after firing
  `TaskStarting`, the written `diagram.html` contains a `"running"` status entry for
  that task's node id.
- `GuardrailFinished_RerendersDiagramWithThatChecksSettledStatus` — a passed vs a
  failed `GuardrailResult` produces `"passed"`/`"failed"` for that check's node id
  specifically (not the whole task).
- `TaskFinished_RerendersDiagramWithTheTasksSettledStatus`.
- `EveryOtherObserverEvent_IsForwardedToInner_Unchanged` — a spy/fake inner observer
  records that each pass-through event was forwarded exactly once with the same
  arguments.
- `ARenderFailure_IsSwallowed_AndDoesNotThrow` — simulate a render failure (e.g. an
  invalid output path) and assert firing an event does not throw (best-effort, mirroring
  `OnTheFlyLogSiteObserver`'s documented behavior).

These must compile and FAIL against the throwing stub — that failure IS the TDD red
`04-implement-diagram-observer` turns green.

## Shared state

Your input state (a snapshot, read-only) is:

```json
{}
```

## Context from completed dependency tasks

Your task depends on the tasks below (directly or transitively); they have already completed. Read their transcripts to see exactly what they produced — files, classes, and conventions — instead of rediscovering the project from scratch. These are read-only context, not work to redo.

- `01-author-tests-diagram-status-overlay-renderer` — Author failing tests + minimal stubs for a per-node status-overlay rendering capability in HtmlDiagramRenderer
  - What it did: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\01-author-tests-diagram-status-overlay-renderer\attempt-3\transcript.md`
- `02-implement-diagram-status-overlay-renderer` — Implement the per-node status-overlay rendering capability in HtmlDiagramRenderer so the authored tests pass
  - What it did: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\02-implement-diagram-status-overlay-renderer\attempt-1\transcript.md`

## Output contract

Write your new/changed state as a single JSON object fragment to this absolute path:

`C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\03-author-tests-diagram-observer\attempt-1\action-out-fragment.json`

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
