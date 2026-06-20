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
`tests/Guardrails.Core.Tests/ParallelValidationGateTests.cs` (class name exactly
`ParallelValidationGateTests`, selected via `--filter "FullyQualifiedName~ParallelValidationGateTests"`).
Exercise the gates THROUGH the existing public `PlanValidator` / plan-load path (so the tests COMPILE
against current code and fail only because the gates are not yet implemented - keep them compiling).
Encode plan 08 M2 (use the canonical code allocation from the plan's diagnostic-codes table):
- A workspace that is NOT a git repository top-level produces diagnostic **GR2015** (error).
- A multi-leaf or fan-in-bearing plan with NO `integrationGate` sink produces **GR2017** (error).
- An `integrationGate` sink with no `scope: "integration"` guardrail produces **GR2018** (error,
  empty integration set).
- A deep worktree-root + deep source on Windows produces the **GR2016** MAX_PATH warning (assert the
  warning code; gate the OS-specificity appropriately).
- Triad teardown: a plan that still declares `captureHashes`/`restoreOnRetry`/`exclusive` is handled
  per the teardown decision (the two triad validators `ValidateCaptureHashes`/`ValidateRestoreOnRetry`
  no longer run, and GR2013/GR2014 no longer carry their triad meanings). Pin whichever behaviour the
  plan settles on (ignore-the-fields vs fail-loudly) - if the plan is ambiguous on this point, encode
  "the fields are ignored, no triad diagnostic is emitted".

These tests MUST fail against current code (the gates are not implemented; the triad validators still
run). Do NOT implement the validator changes - tests only, in this one file. Publish nothing to state.
