## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-03-provision-what-we-prescribe/03-record-injected-grants-in-provenance": { "k": "v" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Injection buys determinism at the cost of transparency: the effective permission set no longer matches
what task.json declares. Close that in BOTH channels - the decision of record is "both", because the
failure this whole plan corrects is "nobody could see it".

1. Extend the attempt provenance record in `src/Guardrails.Core/Journal/JournalModel.cs` (grep for
   AttemptProvenance) with the harness-INJECTED tool grants, beside the existing recorded model
   (extends #198).
2. Echo the injected grants in the attempt log header written by
   `src/Guardrails.Core/Execution/TaskExecutor.cs`, so a human reading logs sees them without querying.

Both must distinguish what the HARNESS ADDED from what the PLAN DECLARED. Keep the change additive - do
not alter existing provenance fields.

**Note on the surrounding code:** this reflects the plan-authoring-time state, before this wave's
earlier tasks had run - verify the injection seam is still shaped as described, and grep for markers
rather than trusting any line number.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Journal/JournalModel.cs` and
`src/Guardrails.Core/Execution/TaskExecutor.cs`. An out-of-scope edit fails the task immediately and
consumes a retry.
