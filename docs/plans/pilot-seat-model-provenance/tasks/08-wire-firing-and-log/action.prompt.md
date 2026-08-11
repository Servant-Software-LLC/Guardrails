## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "08-wire-firing-and-log": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make the `ModelProvenanceFiringTests` pass by wiring the resolved model into the execution path — in the
REAL `TaskExecutor` (do not satisfy the test by any other route).

Once a prompt attempt yields its `PromptResult` and the resolved model is known (`PromptResult.ResolvedModel`
— grep for `ResolvedModel`, do NOT rely on line numbers, they will have moved):
1. **Fire the observer event** — call `observer.AttemptModelResolved(task, attempt, resolvedModel)` near
   where the attempt's other observer notifications are raised. Skip firing for a script attempt or when
   no model was resolved (null).
2. **Write the model into the attempt log preamble** — add the resolved model to the attempt log header the
   executor writes (on a requested/resolved mismatch, show both, e.g. `requested: X / resolved: Y`).

Additive only: a run that never resolves a model behaves exactly as today. Do NOT edit the test file. If
the authored test is genuinely wrong, write {{"needsHuman": "<why>"}} and stop.