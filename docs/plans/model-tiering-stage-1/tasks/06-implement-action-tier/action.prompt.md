## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `06-implement-action-tier`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-implement-action-tier": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make the tests authored by `05-author-tests-action-tier` PASS by filling real logic over the stubs in
`src/Guardrails.Core/`. Requirements: section **C - #225** items 1 and 4 of
`docs/plans/model-tiering-stage-1.charter.md`.

Mirror the existing `action.model` / `action.maxTurns` implementation -- same file, same pattern. `tier`
accepts `easy|medium|hard`, is OPTIONAL, and `guardrails validate` rejects an unrecognized value.

**Update SSOT `docs/plans/02-schemas-and-contracts.md` section 3 in this SAME change** -- the plan requires
code and SSOT to land together.

Do **NOT** edit the authored tests. If they are genuinely wrong, write `{"needsHuman": "<why>"}` to the
state-out path and stop.
