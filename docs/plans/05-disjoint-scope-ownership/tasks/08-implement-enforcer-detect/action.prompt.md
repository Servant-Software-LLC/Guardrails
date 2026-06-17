## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement M4 — enforcement, DETECT-ONLY (§5.1, §5.2, §5.3, §10 M4 of
`docs/plans/05-disjoint-scope-ownership.md`) so the `WorkspaceScopeEnforcer` tests pass.
Detection ships BEFORE revert (M5) deliberately (§9 blast-radius); do NOT revert here:

- Create `src/Guardrails.Core/Execution/WorkspaceScopeEnforcer.cs` (mirror the
  `CapturedFileStore` / `FileHashCapture` factoring; reuse the `FileHashCapture`
  SHA-256 primitive and `WorkspaceContainment`):
  - `Snapshot(workspace, writeScope, enforcementIgnore)` → a pre-image of the
    non-ignored workspace tree (content hashes).
  - A post-action DETECT that compares current bytes to the pre-image and reports any
    out-of-scope write (created/modified/deleted outside the writeScope). Content-based
    (a no-op touch is not a violation). Honor `enforcementIgnore`.
- Wire the seam in `src/Guardrails.Core/Execution/TaskExecutor.cs` `RunAttemptAsync`,
  in the place the design names (replacing the role of the current captureHashes block):
  snapshot pre-action, and after a SUCCEEDED action, if there are out-of-scope writes,
  FAIL the attempt with an actionable message ("out-of-scope writes: <paths>"). No
  revert — just detect-and-fail.
- Add `enforcementIgnore` (with the documented defaults: `state/`, `.git/`,
  `**/bin/**`, `**/obj/**`, `**/node_modules/**`) to
  `src/Guardrails.Core/Model/RunConfig.cs` and the guardrails.json loader.

Make the `FullyQualifiedName~WorkspaceScopeEnforcer` tests pass WITHOUT modifying the
test file. Do NOT add revert, `state/scope-baseline/`, or the `--fresh` wipe — those
are M5. If a test contradicts the design doc, write {"needsHuman": "<why>"} and stop.
Publish nothing to state.
