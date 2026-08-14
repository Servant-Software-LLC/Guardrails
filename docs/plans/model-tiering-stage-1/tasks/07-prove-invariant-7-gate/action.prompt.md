## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `07-prove-invariant-7-gate`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-prove-invariant-7-gate": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Prove **DoR Invariant 7** -- "breaking down a plan against a no-`routing` config produces a folder
byte-identical to today" -- using **BOTH** mechanisms. That "both" is a settled decision recorded in the
plan's resolved `invariant-7-proof` question; do not implement only one.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/ModelTiering/` and `tests/Guardrails.Integration.Tests/Fixtures/no-routing-golden/`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths -- including changes to other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file --
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

1. **The golden.** Commit a golden task folder generated from a no-`routing` config under
   `tests/Guardrails.Integration.Tests/Fixtures/no-routing-golden/`, and a meta-test asserting a fresh
   breakdown against that same config reproduces it **byte-for-byte**. The golden catches drift that no
   enumerated assertion anticipated.
2. **The negative assertions.** A test that runs breakdown against a no-`routing` config and asserts that
   **no `action.tier`, no `tiering` block, and no classification report line** appears anywhere in the
   output. These say plainly what must never appear, which a golden diff states only implicitly.

Tag the class `[Trait("Category","ModelTieringStage1")]`.

Both must PASS: the gate is implemented by task `08`, which is upstream of this one. If they fail, the gate
is wrong -- fix nothing here; write `{"needsHuman": "<what the gate is emitting that it should not>"}` to
the state-out path and stop, because the fix belongs in the skill, not in the proof.
