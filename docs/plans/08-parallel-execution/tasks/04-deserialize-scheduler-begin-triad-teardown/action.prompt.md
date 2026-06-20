## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Complete plan 08 M1's de-serialize + begin-triad-teardown step (this is a removal/refactor, so it has
no dedicated new unit test - the upstream `WorktreeProviderSeamTests` overlap test plus the structural
checks below are the verification):
- In `src/Guardrails.Core/Execution/TaskExecutor.cs`, remove the `RestoreAncestorCaptures` capture/
  restore-seam invocation (the M1 part of the triad teardown). Do NOT yet delete `CapturedFileStore`/
  `FileHashCapture` themselves (M2 finishes the teardown) - only remove the executor's call into the
  restore seam so the executor no longer drives capture/restore.
- In `src/Guardrails.Core/Execution/Scheduler.cs`, drop the `exclusive` admission gate so independent
  tasks are no longer serialized by the exclusive flag (worktree isolation will replace it).

Keep the whole solution building and the existing test suite green (the seam suite must still pass).
Do NOT implement real git worktrees, the write-scope check, or integration. If removing the seam call
breaks a test in a way that needs a human judgement call, emit `{"needsHuman": "<why>"}`. Publish
nothing to state.
