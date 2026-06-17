## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement M2 (§4.2, §4.3, §4.5, §10 of `docs/plans/05-disjoint-scope-ownership.md`)
so the `ScopeLock`/`ScopeScheduler` tests pass:

- Create `src/Guardrails.Core/Execution/ScopeLock.cs`: a `ScopeLock` that generalizes
  `WorkspaceLock` (the binary shared/exclusive lock is the special case `**` =
  exclusive). Same FIFO-fairness discipline; admission keyed on scope intersection
  with currently-held scopes (use `WriteScope.Overlaps`), strict FIFO, no skip-ahead.
  You may replace `WorkspaceLock.cs` or keep it as a thin shim — your call, as long as
  the Scheduler uses `ScopeLock`.
- Rewire `src/Guardrails.Core/Execution/Scheduler.cs` to resolve each ready task's
  `writeScope` and admit it through `ScopeLock` on disjointness; `maxParallelism` still
  caps workers independently (the two gates compose).
- Add the `writeScope` field to `src/Guardrails.Core/Model/TaskNode.cs` and the loader
  (`src/Guardrails.Core/Loading/PlanLoader.cs` / `PlanJson.cs`): an ordered list of
  workspace-relative globs; an ABSENT field resolves to universal `["**"]`.
- Remove the `exclusive` field from `TaskNode` and the loader (clean removal, no
  back-compat): `exclusive: true` is re-expressed as `writeScope: ["**"]`.

Make the `FullyQualifiedName~ScopeLock` and `FullyQualifiedName~ScopeScheduler` tests
pass WITHOUT modifying the test files. Do NOT touch the WriteScope tests/implementation
from M1. Do NOT add enforcement/revert (that is M4/M5). If a test contradicts the
design doc, write {"needsHuman": "<why>"} and stop rather than editing it.

Note: this plan FOLDER itself is executed by an older harness that does NOT understand
a `writeScope` field on task.json — do not be confused by its absence in this folder's
own task.json files; you are ADDING that field to the harness's schema/loader, for
future plans. Publish nothing to state.
