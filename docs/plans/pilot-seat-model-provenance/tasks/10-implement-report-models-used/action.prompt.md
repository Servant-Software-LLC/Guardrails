## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "10-implement-report-models-used": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make the `ReportModelsUsedTests` pass and surface the models-used line to the user.

1. **`RunReport.cs`** — implement `ModelsUsed()`: the distinct, non-null resolved models across the run's
   attempt provenance (use `AttemptProvenance.Model`/`ResolvedModel`), deduped, empty when none.
2. **`RunCommand.cs`** — render a concise "models used" line in the end-of-run summary from
   `RunReport.ModelsUsed()`. Additive: when `ModelsUsed()` is empty (a script-only run), print exactly
   today's summary with no extra line.

Do NOT edit the test file. If the tests are wrong, write {{"needsHuman": "<why>"}} and stop.