## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `04-implement-registry-kind-dispatch`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "04-implement-registry-kind-dispatch": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make the tests authored by `03-author-tests-registry-kind-dispatch` PASS by filling real dispatch logic over
the stubs in `src/Guardrails.Core/Prompts/`. Requirements: section **A - #224** item 2 of
`docs/plans/model-tiering-stage-1.charter.md`.

`PromptRunnerRegistry.FromConfig` switches on `kind`. Only `ClaudePromptRunner` is real in this stage --
concrete non-Claude runners are #223. An unimplemented kind **fails registry construction with an actionable
message naming the kind**, and **never silently falls back to Claude**.

Note what the plan says about WHERE this failure belongs: registry construction is the **backstop, not the
gate**. Failing here means a run is already in flight. Do not treat it as the only defence -- the pre-run
availability check is deliverable D, task `09`.

Do **NOT** edit the authored tests. If they are genuinely wrong or incompatible with the plan, write
`{"needsHuman": "<why>"}` to the state-out path and stop -- an out-of-scope edit to a test file fails the
write-scope check and burns a retry.
