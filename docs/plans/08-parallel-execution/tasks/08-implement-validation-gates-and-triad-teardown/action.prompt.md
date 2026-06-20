## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 M2's validation gates and complete the triad teardown so
`ParallelValidationGateTests` pass:
- `Loading/PlanValidator.cs` + `Loading/DiagnosticCodes.cs`: add the **fresh** codes **GR2015**
  (workspace not a git top-level), **GR2016** (Windows MAX_PATH warning), **GR2017** (multi-leaf /
  fan-in plan missing an `integrationGate` sink), **GR2018** (an `integrationGate` sink with no
  `scope: "integration"` guardrail). RETIRE the GR2013/GR2014 triad meanings in this same change
  (record the retirement in a code comment; do not reuse the numbers for the new gates).
- Triad teardown part 2: DELETE `Execution/WorkspaceLock.cs` and the `exclusive` admission gate;
  remove `TaskNode.Exclusive`, `TaskNode.CaptureHashes`, `TaskNode.RestoreOnRetry`; delete the two
  validators `ValidateCaptureHashes`/`ValidateRestoreOnRetry`. (The `CapturedFileStore`/`FileHashCapture`
  store classes may be deleted here too or left dead for a later sweep - at minimum nothing must still
  reference them.)
- `Model/TaskNode.cs`: add the `IntegrationGate` field (the terminal-gate marker).

Make `ParallelValidationGateTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Re-baseline any of the ~147 triad test references this teardown breaks so
the Core test project builds. Keep `src/Guardrails.Core` building. Publish nothing to state.
