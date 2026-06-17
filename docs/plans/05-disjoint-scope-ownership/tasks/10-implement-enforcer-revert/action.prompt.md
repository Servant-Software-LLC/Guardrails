## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement M5 — enforcement REVERT (§5.3, §5.4, §6, §9, §10 M5 of
`docs/plans/05-disjoint-scope-ownership.md`) so the `ScopeRevert` tests pass:

- Add `RevertOutOfScope(workspace, writeScope, preImage)` to
  `src/Guardrails.Core/Execution/WorkspaceScopeEnforcer.cs`:
  - out-of-scope CREATED file → delete it;
  - out-of-scope MODIFIED file → restore pre-attempt bytes;
  - out-of-scope DELETED file → restore pre-attempt bytes;
  - KEEP all in-scope changes (preserves the failed-attempt "fix, don't restart"
    behavior).
  - Byte baseline: TRACKED file → `git checkout -- <path>`; UNTRACKED file → a lazy
    byte snapshot under `state/scope-baseline/<path>` (harness-owned). Re-check
    `WorkspaceContainment` before every restore write. Log every revert (and any file
    that could not be reverted) to `scope-enforcement.log` in the attempt dir.
- Wire the revert into `src/Guardrails.Core/Execution/TaskExecutor.cs` at the seam
  (§5.1): after a succeeded action, revert out-of-scope writes, then FAIL the attempt
  if there were any (revert already applied — honest halt). Cancellation handling per
  §5.3 (idempotent; the next attempt's pre-action snapshot completes a partial revert).
- Wipe `state/scope-baseline/` in `src/Guardrails.Core/State/RunReset.cs` on
  `--fresh`/`reset` (the `state/captured/` precedent).

Make the `FullyQualifiedName~ScopeRevert` tests pass WITHOUT modifying the test file.
Do NOT yet remove captureHashes/tests-untouched/restoreOnRetry from the schema/loader
— that clean removal is M7. If a test contradicts the design doc, write
{"needsHuman": "<why>"} and stop. Publish nothing to state.
