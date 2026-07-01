## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `02-implement-reverifier-wiring`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-implement-reverifier-wiring": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Change the `SchedulerFactory` composition root (`src/Guardrails.Core/Execution/SchedulerFactory.cs`) to
construct and wire the attempt-decoupled `IReVerifier` seam (`GuardrailReVerifier`) **unconditionally** —
in BOTH serial (`MaxParallelism == 1`) and worktree (`MaxParallelism > 1 && git`) mode (Deliverable 1).

Today the re-verifier is constructed only inside the worktree guard
(`if (plan.Config.MaxParallelism > 1 && IsGitRepository(plan.Workspace))`, line ~92; construction
`reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);` at line ~101). Move/extend the
construction so a non-null `IReVerifier` is always passed to the `Scheduler`. The pre-DAG, terminal, and
task-level preflight phases (later deliverables) all reuse this seam, so if it is null at
`MaxParallelism: 1` they silently no-op in serial mode — a hidden false-green. Keep the worktree-only
construction of the OTHER collaborators (`GitWorktreeProvider`, `aiMergeWorker`) inside their existing
guard — ONLY the `IReVerifier` wiring becomes unconditional. `GuardrailReVerifier` needs the
`processRunner` and the `interpreterMap`, both already available in `Create(...)`.

Make the `ReVerifierWiringTests` (authored upstream, in `tests/Guardrails.Integration.Tests`) pass
WITHOUT modifying them — editing that test file is outside this task's writeScope and fails the harness's
git-diff check. If the authored tests are genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` rather than editing them.

The repo builds with `TreatWarningsAsErrors=true` — keep the change warning-clean. Do not regress the
existing suite. Publish nothing to state.
