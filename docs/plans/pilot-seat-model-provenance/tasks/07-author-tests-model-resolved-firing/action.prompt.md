## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "07-author-tests-model-resolved-firing": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Write a red integration test proving the harness FIRES `AttemptModelResolved` and records the model in
the attempt log when a real prompt attempt runs — driving the REAL execution path (a fake `claude`
runner), never injecting the event by hand (the #120 wiring discipline).

**File (create ONLY this):** `tests/Guardrails.Integration.Tests/ModelProvenanceFiringTests.cs`.

Mirror the existing fake-claude integration tests in this project (grep for the fake/stub prompt-runner
harness they use). The test must:
1. Configure a fake `claude` runner whose `stream-json` output includes a `system`/`init` line carrying a
   known model (e.g. `claude-sonnet-5`), then run a single prompt attempt through the real execution path
   with a recording `IRunObserver` attached.
2. Assert the recording observer received `AttemptModelResolved` with that exact model.
3. Assert the written attempt log contains the resolved model string.

This MUST fail against current code (nothing fires the event or logs the model yet). Do NOT modify
production code to make it pass; the next task does the wiring.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/ModelProvenanceFiringTests.cs`. After this task completes the harness runs
a `git diff` check and rejects any edit outside these paths — including other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write {"needsHuman": "<what is missing>"} to the state-out path and stop.