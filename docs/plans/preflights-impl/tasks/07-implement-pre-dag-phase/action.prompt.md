## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `07-implement-pre-dag-phase`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-implement-pre-dag-phase": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Implement the pre-DAG plan-preflight phase (Deliverable 3) so the `PlanPreflightPhaseTests` pass, and land
SSOT §7's pre-DAG resume rule in the SAME change (invariant 4).

Put the phase logic in a NEW dedicated file (e.g. `src/Guardrails.Core/Execution/PlanPreflightPhase.cs`) and
make only a MINIMAL call-site edit in `Scheduler.cs` — this avoids colliding with the terminal-phase change
on `Scheduler.cs`. Behavior:
- After load/validate and **before** the Scheduler builds waves, evaluate `<plan>/preflights/` **once**
  against the starting repo (the integration worktree on the plan branch at the user's HEAD; serial mode: the
  plan workspace), read-only, via the existing `IReVerifier` seam (now wired unconditionally by Deliverable 1
  — reuse it, do NOT construct a new runner). Runs once regardless of `MaxParallelism`.
- **On PASS:** record the **B1 marker** — the new top-level `planPreflights` section in `state/run.json`
  (OUTSIDE `tasks{}`): `{ "status": "passed", "planHash": "sha256:…", "evaluatedAt": "…", "checks": [ … ] }`,
  where `planHash` is the SAME `PlanHash` the journal/§13 review marker use.
- **On FAIL:** halt BEFORE scheduling — no task runs, zero tokens spent. Outcome `plan-preflight-failed`,
  journaled in `planPreflights` (`status: "failed"`), process **exit 2**.
- **Resume SKIP rule (the load-bearing B1 fix):** the resume pre-pass reads `planPreflights`; if
  `status == "passed"` AND its `planHash` matches the current `PlanHash`, the pre-DAG phase is **SKIPPED**
  (not re-run) — so a NEGATIVE baseline is evaluated exactly once across the run lifecycle and a mid-DAG
  crash + resume never re-runs it against partially-merged bytes and false-halts.
- **Re-run only on `--fresh` or `planHash` mismatch.** `--fresh` clears `run.json` (marker clears); a
  `planHash` mismatch forces re-evaluation.
- The CLI maps a `plan-preflight-failed` halt to exit 2 (`src/Guardrails.Cli/`, `ExitCodes.TaskFailed`).

SSOT edit (`docs/plans/02-schemas-and-contracts.md` §7, SAME change): document the pre-DAG resume rule (SKIP
on a matching `planPreflights` marker) and the `plan-preflight-failed` → halt-before-scheduling → exit 2 flow.

Make the `PlanPreflightPhase` tests pass WITHOUT modifying them (editing `tests/**` is outside this task's
writeScope). If they are genuinely wrong, write `{"needsHuman": "<why>"}`. Do not regress the existing suite;
the per-union §4.3 re-verify and terminal gate must still work. Keep warning-clean
(`TreatWarningsAsErrors=true`). Publish nothing to state.
