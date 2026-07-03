## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `06-author-tests-pre-dag-phase`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-author-tests-pre-dag-phase": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Author failing INTEGRATION tests for the pre-DAG plan-preflight phase (Deliverable 3, test-groups #1/#2/#3).
Drive the REAL run entry / scheduler over committed fixture plans and assert OBSERVABLE outcomes (the
journal's `planPreflights` section, the process exit code, and that zero task attempts were journaled) —
do NOT assume any new public API on the scheduler. The tests must be RED against current code (there is no
pre-DAG phase yet) and go green once the phase is implemented.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/PlanPreflightPhaseTests.cs`. After this task the harness runs a `git
diff` check and rejects any edit outside that path — production files, other tests, the `.csproj`. An
out-of-scope edit fails the task and consumes a retry. If a compile error comes from a missing symbol in
another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` and stop.

Tests to author (tag the class `[Trait("Category","Preflights")]`; run **each in BOTH serial `MaxParallelism=1`
AND worktree `MaxParallelism>1` mode** — a `[Theory]` over the two modes is ideal; the serial-mode
`IReVerifier` wiring is exactly where a false-green hides):
1. **Plan-preflight red → zero-token halt (#1):** a fixture with a RED `<plan>/preflights/` check → the run
   halts BEFORE any task runs; process **exit 2**; the journal shows **zero attempts**;
   `planPreflights.status == "failed"`.
2. **Green pre-DAG:** a fixture with a GREEN `<plan>/preflights/` → `planPreflights.status == "passed"` with a
   `planHash` matching the current `PlanHash`; the DAG then schedules normally.
3. **B1 negative-baseline resume SKIP (#2):** a fixture whose `<plan>/preflights/` includes a check that is
   true only at the START (absence of an artifact a task then introduces). Pass it, simulate a mid-DAG
   interruption, then resume with a plain run → the pre-DAG phase is **SKIPPED** (the negative check is NOT
   re-evaluated, no false-halt), evidenced by the `planPreflights` marker read and no re-evaluation.
4. **`--fresh` re-runs pre-DAG (#3):** after a passed marker, a `--fresh` run re-evaluates the pre-DAG phase.

REUSE the existing integration-test harnesses/builders (`ScriptPlanBuilder` / `FakeClaudePlanBuilder`, the
`GitWorktreeLifecycle` fixtures for worktree mode, the resume/reset patterns in `ResumeAndResetRetryTests.cs`,
`StringConsoleIo`) — do NOT hand-roll a git repo, a plan builder, or a resume harness. The fixture plans need
a plan-level `<plan>/preflights/` folder (the loader now understands it) with a deterministic guardrail script.

The tests MUST **compile** and **fail** (no pre-DAG phase exists yet) — failing is intentional, NOT compiling
is a mistake. Do NOT implement the phase. Keep warning-clean (`TreatWarningsAsErrors=true`). Publish nothing
to state.
