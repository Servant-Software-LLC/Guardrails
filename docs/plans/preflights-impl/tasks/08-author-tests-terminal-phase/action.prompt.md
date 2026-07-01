## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `08-author-tests-terminal-phase`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-author-tests-terminal-phase": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Author failing INTEGRATION tests for the terminal plan-guardrail phase (Deliverable 4, test-groups
#4/#5/#6). Drive the REAL run entry over committed fixture plans and assert observable outcomes (the
journal's `planGuardrails` section, the process exit code, the durable plan branch, and the
`--revalidate-task plan:guardrails` behavior). RED against current code (no terminal `<plan>/guardrails/`
phase yet — today the terminal gate is an `integrationGate` task run), green once implemented.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/PlanGuardrailPhaseTests.cs`. After this task the harness runs a `git
diff` check and rejects any edit outside that path. An out-of-scope edit fails the task and consumes a
retry. If a compile error comes from a missing symbol elsewhere, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.

Tests to author (tag the class `[Trait("Category","Preflights")]`; run **each in BOTH serial `MaxParallelism=1`
AND worktree `MaxParallelism>1` mode**):
1. **Plan-guardrail red → durable terminal halt (#4):** a fixture with a RED `<plan>/guardrails/` check → the
   terminal phase halts AFTER the DAG drains green; **exit 2**; `planGuardrails.status == "failed"`; the plan
   branch still carries ALL task work (durable).
2. **B2(a) revalidate (#6):** after a red terminal gate, hand-fix the merged HEAD, then
   `--revalidate-task plan:guardrails` runs ONLY the `<plan>/guardrails/` checks against the current merged
   HEAD → green settle, **exit 0**; a still-red gate → `plan-guardrail-failed`, exit 2. (`plan:guardrails` is
   a reserved synthetic id; the `:` can never collide with a real `stableId`/folder id.)
3. **B2(b) terminal-only resume (#5):** DAG all-green + terminal red + a plain resume run → every task SKIPs
   via the existing resume rule (no attempt burned) and ONLY the terminal phase re-fires on the current
   merged HEAD.
4. The per-union §4.3 re-verify still fires at unions during the run (no regression — the terminal folder did
   NOT absorb the per-union set).

REUSE the existing integration harnesses (`ScriptPlanBuilder`, the `GitWorktreeLifecycle` fixtures, the
resume/revalidate patterns in `ResumeAndResetRetryTests.cs` / any existing terminal-gate tests,
`StringConsoleIo`) — do NOT hand-roll a git repo, plan builder, or resume harness. The fixture plans need a
plan-level `<plan>/guardrails/` folder with a deterministic guardrail script.

The tests MUST **compile** and **fail** (no terminal `<plan>/guardrails/` phase exists yet). Failing is
intentional, NOT compiling is a mistake. Do NOT implement the phase. Keep warning-clean
(`TreatWarningsAsErrors=true`). Publish nothing to state.
