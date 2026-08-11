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

## Shared state

Your input state (a snapshot, read-only) is:

```json
{
  "03-author-tests-diagram-observer": {
    "produced": true
  }
}
```

## Context from completed dependency tasks

Your task depends on the tasks below (directly or transitively); they have already completed. Read their transcripts to see exactly what they produced — files, classes, and conventions — instead of rediscovering the project from scratch. These are read-only context, not work to redo.

- `01-author-tests-diagram-status-overlay-renderer` — Author failing tests + minimal stubs for a per-node status-overlay rendering capability in HtmlDiagramRenderer
  - What it did: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\01-author-tests-diagram-status-overlay-renderer\attempt-3\transcript.md`
- `02-implement-diagram-status-overlay-renderer` — Implement the per-node status-overlay rendering capability in HtmlDiagramRenderer so the authored tests pass
  - What it did: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\02-implement-diagram-status-overlay-renderer\attempt-1\transcript.md`
- `03-author-tests-diagram-observer` — Author failing tests + minimal stubs for a new OnTheFlyDiagramObserver decorator
  - What it did: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\03-author-tests-diagram-observer\attempt-4\transcript.md`

## Output contract

Write your new/changed state as a single JSON object fragment to this absolute path:

`C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\04-implement-diagram-observer\attempt-2\action-out-fragment.json`

Write ONLY your own keys (conventionally namespaced under your task id). Do NOT modify state.json directly — the harness is the single writer and merges your fragment after guardrails pass. If you have nothing to contribute, write nothing.

If you cannot proceed without a human decision, write exactly `{ "needsHuman": "<your question>" }` to that same path and stop — the harness will escalate to a human without burning further retries.

## Previous attempt failed

# Attempt 1 of task '04-implement-diagram-observer' failed

Task: Implement OnTheFlyDiagramObserver so the authored tests pass

Fix the specific problems below. Do NOT start over from scratch — keep what
already works and address only what failed.

## Write-scope violation

The following path(s) were modified but fall OUTSIDE this task's declared writeScope:
- `outside.txt`
- `src/output.txt`

The harness has already reverted those files to their pre-attempt state. Your
in-scope changes are preserved. On retry, ensure you only write to paths covered
by this task's writeScope (SSOT §3.4, plan 08 §2).

This is a RETRY. Fix these specific problems; do not start over — keep what already works and address only what failed above.

### Prior attempt logs (read-only — inspect for full context)

Earlier attempts and their logs, most recent first. Read the transcript to see what each attempt did, and the feedback for why it failed:

- Attempt 1 (guardrail-failed): `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\04-implement-diagram-observer\attempt-1`
  - What it did: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\04-implement-diagram-observer\attempt-1\transcript.md`
  - Why it failed: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\04-implement-diagram-observer\attempt-1\feedback.md`

## Worktree safety

You are running in an isolated git worktree dedicated to this task. `git stash` is **NOT safe** to use here: the stash stack (`refs/stash`) is repo-wide, not scoped to this worktree — a concurrent task (or a human's own diagnostic worktree) doing its own `git stash` around the same time can silently overwrite or steal yours, and a later `git stash pop` can apply the WRONG entry into this tree. Attempting to use `git stash` here will be blocked.

If you need to test against a clean baseline and restore your changes afterward, use this stash-free, entirely LOCAL alternative instead:

```
git diff > /tmp/mine.patch
git checkout -- <files>      # test the baseline
git apply /tmp/mine.patch    # restore your changes
```
