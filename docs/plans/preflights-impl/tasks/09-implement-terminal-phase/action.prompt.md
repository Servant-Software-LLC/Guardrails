## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `09-implement-terminal-phase`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-implement-terminal-phase": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Implement the terminal plan-guardrail phase (Deliverable 4) so the `PlanGuardrailPhaseTests` pass, and land
SSOT §7.1 in the SAME change (invariant 4). Put the phase logic in a NEW dedicated file (e.g.
`src/Guardrails.Core/Execution/PlanGuardrailPhase.cs`) and make only a MINIMAL edit at the existing terminal
gate call-site in `Scheduler.cs` (lines ~231–253) — the pre-DAG phase task already touched `Scheduler.cs`, so
keep this edit surgical.

Behavior:
- After every task settles green, evaluate `<plan>/guardrails/` **once** on the merged plan-branch HEAD via
  the existing attempt-decoupled `IReVerifier` seam (the same seam the §4.3 per-union re-verify and today's
  terminal gate use — NO new runner). This **replaces** the terminal `integrationGate` task run
  (`Scheduler.cs` ~231–253).
- **On FAIL:** terminal halt. Outcome `plan-guardrail-failed`, journaled in the top-level `planGuardrails`
  section (OUTSIDE `tasks{}`): `{ "status": "failed", "planHash": "…", "failedChecks": [ … ] }`, **exit 2**.
  The work is durable on the plan branch; carry over the merge-collision attribution.
- **B2(a) — revalidate via a synthetic id (NOT a new verb).** The existing `--revalidate-task <id>`
  (`src/Guardrails.Cli/Commands/Revalidate.cs` + `RunCommand.cs`) must accept the reserved synthetic id
  **`plan:guardrails`**: it runs ONLY the `<plan>/guardrails/` checks against the current merged HEAD
  (`IReVerifier` pointed at the integration worktree the harness owns). Pass ⇒ terminal phase settles green,
  exit 0; fail ⇒ `plan-guardrail-failed`, exit 2. Also mint `plan:preflights` (the preflight analogue) for
  symmetry — resolves plan note #4 (ship both; low cost). The `:` is already disallowed in a real id
  (§3 `^[a-z0-9][a-z0-9._-]*$`), so these can never collide with a real task.
- **B2(b) — terminal-only resume.** On a plain resume where EVERY task is `succeeded` but
  `planGuardrails.status == "failed"` (and `planHash` matches): all tasks SKIP via the existing resume rule
  (no attempt burned), then the resume pre-pass re-fires ONLY the terminal phase on the current merged HEAD. A
  `planHash` mismatch falls back to a normal resume.
- Do NOT change the per-union §4.3 re-verify — it must still fire at unions during the run (the terminal
  folder is terminal-only, NOT the per-union set).

SSOT edit (`docs/plans/02-schemas-and-contracts.md` §7.1, SAME change): document `--revalidate-task
plan:guardrails` (and `plan:preflights`) as reserved synthetic ids and the exit-2 narrative for the terminal
halt; add the terminal-only resume rule to §7.

Make the `PlanGuardrailPhase` tests pass WITHOUT modifying them (editing `tests/**` is out of scope). If they
are genuinely wrong, write `{"needsHuman": "<why>"}`. Do not regress the existing suite. Keep warning-clean
(`TreatWarningsAsErrors=true`). Publish nothing to state.
