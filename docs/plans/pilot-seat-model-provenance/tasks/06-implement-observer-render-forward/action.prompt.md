## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "06-implement-observer-render-forward": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make the `ObserverModelResolvedTests` pass and surface the resolved model in the live UI.

1. **`OnTheFlyLogSiteObserver.cs`** and **`OnTheFlyDiagramObserver.cs`** — override `AttemptModelResolved`
   to forward to the inner observer (`_inner.AttemptModelResolved(task, attempt, resolvedModel)`), the same
   pattern these decorators use for the other `IRunObserver` methods.
2. **`LiveRunObserver.cs`** — override `AttemptModelResolved` to show the resolved model on the running
   task/attempt row (and a requested/resolved mismatch when the harness flags one). Additive — do not
   change existing rendering for runs that never resolve a model.
3. **`ConsoleRunObserver.cs`** — override `AttemptModelResolved` to print a concise line in the non-live path.

Do NOT edit the test file or `IRunObserver.cs`. If the tests are wrong, write {{"needsHuman": "<why>"}} and stop.