## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author xUnit.v3 tests in the single new file
`tests/Guardrails.Integration.Tests/MergeOnSuccessTests.cs` (class name exactly `MergeOnSuccessTests`,
selected via `--filter "FullyQualifiedName~MergeOnSuccessTests"`). Encode plan 08 §5 / Stage-2 BEFORE
`--merge-on-success` exists:
- A green run with `--merge-on-success` (or `mergeOnSuccess: true`) merges the plan branch
  `guardrails/<plan-name>` into the user's ORIGINAL branch - `git merge --ff-only` when the user's
  branch has not advanced, else a real merge whose re-verify must pass.
- **AI-merge is WITHHELD at this boundary:** a user branch advanced mid-run with a CONFLICTING commit
  → the harness does NOT AI-resolve; it halts to needs-human with the plan branch INTACT and the user's
  branch untouched (no force-overwrite).
- Default OFF: the run leaves the plan branch for the user to review and merge (no merge attempted).

These reference the not-yet-existing `--merge-on-success` flag / `mergeOnSuccess` end-of-run hook, so
the project will not compile against current code - that is the intended "fails on current code"
signal. Do NOT implement it - tests only, in this one file. Publish nothing to state.
