## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `11-implement-task-preflight-slot`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "11-implement-task-preflight-slot": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Implement the task-level preflight slot (Deliverable 5) so the `TaskPreflightSlot` tests pass. Put the
evaluation logic in a NEW dedicated file (e.g. `src/Guardrails.Core/Execution/TaskPreflightGate.cs`) and make
only a MINIMAL edit in `TaskExecutor.ExecuteAsync` (the insertion point is BEFORE the attempt loop `for` at
`TaskExecutor.cs` line ~111, in the setup region ~75–109) — this keeps the diff off `Scheduler.cs`, which the
phase tasks own.

Behavior:
- In `TaskExecutor.ExecuteAsync`, BEFORE the attempt loop, evaluate `tasks/<id>/preflights/` (the
  `TaskNode.Preflights` the loader now populates) via the `IReVerifier` seam (wired unconditionally by
  Deliverable 1) pointed at the CONSUMER's segment worktree at `taskBase`. It gates loop ENTRY — a JIT check
  that the producer this task `dependsOn` actually delivered the type/route/symbol into the inherited bytes,
  before spending an attempt.
- **On PASS:** attempts proceed normally (no behavior change vs today).
- **On FAIL:** short-circuit to `needs-human` **WITHOUT consuming a retry attempt** (the no-burn property),
  in **BOTH** serial and worktree mode (structural, not budget-dependent — do NOT enter the attempt loop, so
  `NextAttemptNumber` is never called for the preflight failure). Outcome `task-preflight-failed` — a per-task
  result inside `tasks{}` (added to the outcome model by Deliverable 6), distinct from the plan-level
  `plan-preflight-failed`. Block ONLY this task and its transitive dependents via the existing scheduler
  closure; independent branches keep running.

Make the `TaskPreflightSlot` tests pass WITHOUT modifying them (editing `tests/**` is out of scope). If they
are genuinely wrong, write `{"needsHuman": "<why>"}`. Do not regress the existing suite (existing attempt-loop
/ retry / needs-human behavior must be unchanged when there is no `tasks/<id>/preflights/` folder). Keep
warning-clean (`TreatWarningsAsErrors=true`). Publish nothing to state.
