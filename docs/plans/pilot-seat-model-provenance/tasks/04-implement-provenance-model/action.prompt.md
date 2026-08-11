## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "04-implement-provenance-model": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make the `AttemptProvenanceModelTests` pass and record the real model in provenance.

1. **`PromptExecutionSupport.cs`** — fill the two helper stubs:
   - `ResolveActualModel(resolvedModel, requestedModel)` -> `resolvedModel ?? requestedModel ?? "(cli
     default)"` (reuse the exact sentinel `ResolveModelForDisplay` uses).
   - `IsModelMismatch(resolvedModel, requestedModel)` -> true only when BOTH non-null and differ
     (case-insensitive); a null resolved is unknown = not a mismatch.
2. **`AttemptJournaler.cs`** — when it builds the `AttemptProvenance` for a prompt attempt (grep for where
   `Model` / `AttemptProvenance` is set), populate from the attempt `PromptResult`: `ResolvedModel` =
   `PromptResult.ResolvedModel`; `RequestedModel` = today's `ResolveModelForDisplay` value; `Model` =
   `ResolveActualModel(resolved, requested)` (best-known-actual, not the config guess). A script attempt
   keeps `Model` null as today.

Do NOT edit the test file or `JournalModel.cs`. If the tests are wrong, write {{"needsHuman": "<why>"}} and stop.