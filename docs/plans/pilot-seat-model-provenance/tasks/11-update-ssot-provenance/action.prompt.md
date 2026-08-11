## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "11-update-ssot-provenance": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Document the model-provenance contract landed by this plan in the SSOT, in the same change as the code
(SSOT invariant 4).

In `docs/plans/02-schemas-and-contracts.md`, in the journal/provenance section (grep for
`AttemptProvenance` / `provenance`), document the new fields: `resolvedModel` (the CLI-observed actual
model), `effort`, and that `model` is now best-known-actual (`resolved ?? requested ?? "(cli default)"`)
rather than the config guess; note the attempt-log header records the resolved model and that the stream
parse now reads the CLI-echoed model. Keep edits scoped to the provenance/log/stream sections.

Docs only. If a section is ambiguous, write {{"needsHuman": "<question>"}} and stop.