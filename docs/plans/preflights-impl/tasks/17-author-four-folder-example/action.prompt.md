## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  the name of the directory this task.json lives in (here `17-author-four-folder-example`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "17-author-four-folder-example": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific
  failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the
  state-out path and stop.

## Task
Produce the four-folder worked example at `docs/plans/09-preflight-first-class/example/` (Deliverable 10).
NOTE: on this branch that folder does NOT currently exist (the prior example lived on the design-of-record
branch). So **create it fresh** if absent, or recast it if a previous attempt left one — either way the END
STATE is what the guardrails check. Edit ONLY files under `docs/plans/09-preflight-first-class/`.

The example is a small, valid plan folder that exercises all four folder kinds:
- **`example/preflights/`** (plan root) with TWO checks: one POSITIVE baseline (e.g.
  `01-all-repo-tests-green.ps1`) and one NEGATIVE assert-absent baseline (e.g.
  `02-correlation-id-absent.ps1`), each a deterministic byte/exit check following the advisory live-probe
  guidance (process-start / byte checks are FINE; NO network/poll).
- **`example/guardrails/`** (plan root) holding the terminal whole-repo checks (build + full suite + a union
  invariant), carrying **≥1 real integration-set re-run** (the re-homed GR2018 rule — NOT a tautological
  `exit 0` file).
- A **task-level `example/tasks/<id>/preflights/`** keyed to a `dependsOn` edge (the JIT dependency-delivery
  illustration).
- Ordinary `example/tasks/<id>/` with `task.json` + one `action.*` + `tasks/<id>/guardrails/`, and
  `example/guardrails.json`.

Hard constraints (a meta-check greps for these):
- **NO `integrationGate: true` task** anywhere (the terminal checks live in `example/guardrails/`).
- **NO no-op ROOT/END task** and **NO `scope:"precondition"`** marker (no third scope value exists).
- Every guardrail file opens with its `# catches:` comment.

Then VALIDATE and RENDER against the FRESHLY-BUILT CLI (the installed `guardrails` on PATH does NOT understand
the four folders — you MUST use the local build):
- `dotnet run --project src/Guardrails.Cli -- validate docs/plans/09-preflight-first-class/example` → exit 0.
- `dotnet run --project src/Guardrails.Cli -- graph docs/plans/09-preflight-first-class/example` → generates
  `example/diagram.md` (+ `diagram.html`) in the new container model. Commit the generated diagram.
- Add a short `example/README.md` describing the four folders. (If `docs/plans/09-preflight-first-class.md`
  exists, update its prose pointer to this example; it does NOT exist on this branch, so skip that
  sub-step — do not create the design-of-record doc.)

Set the example's `guardrails.json` `"workspace"` to the correct relative depth to the repo root
(`docs/plans/09-preflight-first-class/example/` → `../../../..`). Publish nothing to state.
