## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `10-author-tests-task-preflight-slot`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "10-author-tests-task-preflight-slot": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Author failing INTEGRATION tests for the task-level preflight slot (Deliverable 5, test-group #8). Drive the
REAL run entry / `TaskExecutor` over committed fixture plans and assert observable outcomes (per-task
journaled outcome, the ATTEMPT COUNT, and cone isolation). RED against current code (there is no
`tasks/<id>/preflights/` slot yet), green once implemented.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/TaskPreflightSlotTests.cs`. After this task the harness runs a `git diff`
check and rejects any edit outside that path. An out-of-scope edit fails the task and consumes a retry. If a
compile error comes from a missing symbol elsewhere, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.

Tests to author (tag the class `[Trait("Category","Preflights")]`; run **each in BOTH serial `MaxParallelism=1`
AND worktree `MaxParallelism>1` mode** — the no-burn property is STRUCTURAL, not budget-dependent):
1. **No-burn + needs-human (#8):** a fixture where a consumer's `tasks/<id>/preflights/` check is RED (the
   producer's contribution is absent) → the consumer settles `needs-human` with outcome
   `task-preflight-failed`, and **NO attempt is burned** — assert the attempt count did NOT increment for the
   preflight failure (the slot gates loop ENTRY, before the attempt loop).
2. **Cone-blocking / isolation (#8):** the consumer's transitive dependents are `blocked`, while an
   **independent** branch (not in the consumer's cone) runs to completion.
3. **Exit 2** for the run when a task-preflight blocks a cone.
4. **Green passthrough:** a GREEN `tasks/<id>/preflights/` lets the attempt loop proceed (no behavior change
   vs today).

REUSE the existing integration harnesses (`ScriptPlanBuilder`, the `GitWorktreeLifecycle` fixtures,
`NeedsHumanTriageTests.cs` / blocked-cone patterns) — do NOT hand-roll a git repo or plan builder. The
fixture plans need a task-level `tasks/<id>/preflights/` folder on the consumer, keyed to a `dependsOn` edge,
with a deterministic guardrail script that is RED when the producer's contribution is absent.

The tests MUST **compile** and **fail** (no task-preflight slot exists yet). Failing is intentional, NOT
compiling is a mistake. Do NOT implement the slot. Keep warning-clean (`TreatWarningsAsErrors=true`). Publish
nothing to state.
