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
- `07-author-tests-diagram-search-box` — Author failing tests + minimal stubs for a client-side search box (substring match, highlight, pan/zoom-to-match) in HtmlDiagramRenderer
  - What it did: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\07-author-tests-diagram-search-box\attempt-2\transcript.md`

## Output contract

Write your new/changed state as a single JSON object fragment to this absolute path:

`C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\08-implement-diagram-search-box\attempt-1\action-out-fragment.json`

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
