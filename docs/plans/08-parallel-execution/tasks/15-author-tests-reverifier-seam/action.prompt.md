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
`tests/Guardrails.Core.Tests/ReVerifierSeamTests.cs` (class name exactly `ReVerifierSeamTests`,
selected via `--filter "FullyQualifiedName~ReVerifierSeamTests"`). Encode plan 08 §4.3 / feasibility-fix-2
BEFORE the `IReVerifier` seam exists:
- `IReVerifier` runs a GIVEN guardrail set against arbitrary worktree bytes and returns a pass/fail
  result.
- It is **attempt-decoupled** — name this test method exactly `ReVerify_DoesNotReadActionEnv`: it
  requires NO attempt `logDir`, NO attempt number, and NO action result; assert it does NOT read
  `GUARDRAILS_ACTION_STDOUT` / `_STDERR` / `_RESULT` (it runs where no action ran). Construct a
  guardrail set whose guardrails would observe those env vars and assert they are absent/empty in the
  re-verify context. (The scenarios-present guardrail greps for that exact method name, so the test and
  the guardrail must agree - a bare `GUARDRAILS_ACTION_` mention is no longer sufficient.)
- A passing guardrail set returns pass; a failing one returns fail with the failing guardrail's output.

These tests reference the not-yet-existing `IReVerifier`, so the project will not compile against
current code - that is the intended "fails on current code" signal. Do NOT implement the seam - tests
only, in this one file. Publish nothing to state.
