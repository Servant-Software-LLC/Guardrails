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

`C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\07-author-tests-diagram-search-box\attempt-2\action-out-fragment.json`

Write ONLY your own keys (conventionally namespaced under your task id). Do NOT modify state.json directly — the harness is the single writer and merges your fragment after guardrails pass. If you have nothing to contribute, write nothing.

If you cannot proceed without a human decision, write exactly `{ "needsHuman": "<your question>" }` to that same path and stop — the harness will escalate to a human without burning further retries.

## Previous attempt failed

# Attempt 1 of task '07-author-tests-diagram-search-box' failed

Task: Author failing tests + minimal stubs for a client-side search box (substring match, highlight, pan/zoom-to-match) in HtmlDiagramRenderer

Fix the specific problems below. Do NOT start over from scratch — keep what
already works and address only what failed.

## The previous attempt ran out of turns

The previous attempt hit the max-turns cap and was stopped mid-progress — this is a TURN
BUDGET exhaustion, NOT a logic error. The harness has RAISED the turn budget for this
attempt, but do not waste the headroom:

- The partial work was reverted from your WORKING TREE, but it was NOT discarded — see
  '## Prior attempt work is salvageable' below for how to selectively recover it.
- Work DIRECTLY toward the deliverable. Batch related edits, avoid redundant exploration,
  and don't re-discover what a prior attempt already established.
- Prioritise getting the change to COMPILE and the guardrails to GO GREEN first; refine after.
- If this task genuinely cannot finish within a reasonable turn budget (it bundles several
  distinct sub-features, or needs an expensive one-time setup better done by an upstream task),
  STOP and write {"needsHuman": "<this task is under-budgeted for turns; suggest a split or a
  higher maxTurns>"} to GUARDRAILS_STATE_OUT rather than burning more attempts.

## File writes were also rolled back

Because the state fragment was rejected, all file writes from this attempt were
reverted. On your next attempt, re-author ALL files from scratch — do not assume
any file you wrote in a previous attempt is still present on disk.

## Prior attempt work is salvageable

Attempt 1's FULL working tree — before the reset above — was preserved
to the git ref `refs/guardrails/07-author-tests-diagram-search-box/attempt-1`. That attempt was likely making REAL progress (it ran
out of budget, not out of correctness), so REVIEW it and selectively adopt what's good instead
of re-deriving everything from scratch:

- Inspect what changed: `git show --stat refs/guardrails/07-author-tests-diagram-search-box/attempt-1` or `git diff <taskBase> refs/guardrails/07-author-tests-diagram-search-box/attempt-1`.
- Pull in a file that is CORRECT as-is: `git checkout refs/guardrails/07-author-tests-diagram-search-box/attempt-1 -- <path>`.
- Redo, from scratch, only what is INCOMPLETE or wrong — do not blindly restore every file;
  judge each one.

What that attempt changed (`git diff --stat` vs. this task's base commit):
```
.github/scripts/smoke-packaged-tool.sh             |   0
 src/Guardrails.Cli/packages.lock.json              |  58 +--
 src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs   |  77 +++-
 src/Guardrails.Core/packages.lock.json             |  24 +-
 .../HtmlDiagramRendererTests.cs                    |  81 ++++
 tests/Guardrails.Core.Tests/packages.lock.json     | 450 +++++++++----------
 .../packages.lock.json                             | 486 ++++++++++-----------
 7 files changed, 666 insertions(+), 510 deletions(-)
```

Salvaged files remain subject to this task's declared writeScope, exactly like any other
write this attempt makes — the write-scope check runs on your FINAL state regardless of how
it got there.

This is a RETRY. Fix these specific problems; do not start over — keep what already works and address only what failed above.

### Prior attempt logs (read-only — inspect for full context)

Earlier attempts and their logs, most recent first. Read the transcript to see what each attempt did, and the feedback for why it failed:

- Attempt 1 (max-turns): `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\07-author-tests-diagram-search-box\attempt-1`
  - What it did: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\07-author-tests-diagram-search-box\attempt-1\transcript.md`
  - Why it failed: `C:\Dev AI\Guardrails\docs\plans\diagram-live-status-and-search\logs\2026-07-07T12-19-44Z-861a\07-author-tests-diagram-search-box\attempt-1\feedback.md`

## Worktree safety

You are running in an isolated git worktree dedicated to this task. `git stash` is **NOT safe** to use here: the stash stack (`refs/stash`) is repo-wide, not scoped to this worktree — a concurrent task (or a human's own diagnostic worktree) doing its own `git stash` around the same time can silently overwrite or steal yours, and a later `git stash pop` can apply the WRONG entry into this tree. Attempting to use `git stash` here will be blocked.

If you need to test against a clean baseline and restore your changes afterward, use this stash-free, entirely LOCAL alternative instead:

```
git diff > /tmp/mine.patch
git checkout -- <files>      # test the baseline
git apply /tmp/mine.patch    # restore your changes
```
