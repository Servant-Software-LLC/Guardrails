## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `02-implement-runner-kind-and-axes`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-implement-runner-kind-and-axes": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make the tests authored by `01-author-tests-runner-kind-and-axes` PASS by filling real logic over the stubs
in `src/Guardrails.Core/`. The requirements are section **A - #224** of
`docs/plans/model-tiering-stage-1.charter.md`.

Do **NOT** edit the authored tests. Make them pass by fixing the implementation. If the authored tests are
genuinely wrong or incompatible with the plan, write `{"needsHuman": "<why>"}` to the state-out path and
stop -- an out-of-scope edit to a test file fails the write-scope check and burns a retry.

Two constraints the plan states outright:

- **`kind` defaults to `claude`**, so every existing config validates and runs unchanged. The change is
  ADDITIVE; a config with no `kind` must behave exactly as it does today.
- **`routing.rank` is NOT implemented.** Ordering is ascending `strength` -- the weakest model that can
  serve the tier goes first. A config still carrying `rank` raises a retired-field WARNING; it must never
  silently change ordering.

**Update SSOT `docs/plans/02-schemas-and-contracts.md` section 9 -- the prose AND the canonical-schema
sentinel -- in this SAME change.** The plan requires code and SSOT to land together; a schema change that
ships without its SSOT update is exactly the drift that rule exists to prevent.
