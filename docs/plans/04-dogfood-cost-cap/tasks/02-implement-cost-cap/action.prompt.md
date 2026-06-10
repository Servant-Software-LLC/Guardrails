## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement the per-run cost cap described in `docs/plans/04-dogfood-cost-cap.md` so the
`FullyQualifiedName~CostCap` tests authored in task 01 pass, WITHOUT modifying those test
files. Make these source changes (consult the SSOT `docs/plans/02-schemas-and-contracts.md`
§2/§7 for field placement and `JournalCost` for the existing aggregation):

1. `src/Guardrails.Core/Model/RunConfig.cs` — add `public decimal? MaxCostUsd { get; init; }`
   (null = no cap; SSOT §2). Document it briefly.

2. `src/Guardrails.Core/Loading/RawManifests.cs` + `PlanLoader.cs` — parse `maxCostUsd`
   from `guardrails.json` into `RunConfig.MaxCostUsd` (mirror how `defaultRetries` is
   wired: raw nullable field → mapped through, default null).

3. `src/Guardrails.Core/Loading/DiagnosticCodes.cs` — add
   `public const string CostCapNonPositive = "GR2010";` with an XML-doc summary.
   `src/Guardrails.Core/Loading/PlanValidator.cs` — add a check: when `MaxCostUsd` is
   present and `<= 0`, emit an ERROR with that code (a zero/negative cap halts before any
   work — a config mistake). Keep all existing checks intact.

4. `src/Guardrails.Core/Execution/Scheduler.cs` — before a worker launches a task's
   attempt, if the config has a cap and `JournalCost.Total(<the journal document>) >=
   MaxCostUsd`, do NOT execute the task: settle it `needs-human` with a Summary containing
   "cost cap reached" and block its transitive dependents via the existing halt path (the
   same path used when a task ends non-green). The cap is checked against the journal's
   cumulative cost so resumes account for prior spend. An already-in-flight attempt is NOT
   interrupted. You will need the scheduler to see cumulative cost; extend
   `ISchedulerJournal` minimally if required (e.g. a `decimal CurrentCostUsd()` or expose
   the document) and update `RunJournal` plus the test fake accordingly — but DO NOT change
   the authored CostCap test expectations.

The build runs with `TreatWarningsAsErrors`; 0 warnings tolerated. Do not edit files under
`tests/`. Publish nothing to state.
