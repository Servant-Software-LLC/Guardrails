## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author failing xUnit.v3 tests in
`tests/Guardrails.Core.Tests/WorkspaceScopeEnforcerTests.cs` for M4 — enforcement,
DETECT-ONLY (§5.1, §5.2, §5.3, §10 M4 of
`docs/plans/05-disjoint-scope-ownership.md`). Mirror the existing test conventions,
and reuse the temp-workspace helpers the suite already uses for file-touching tests
(see `CapturedFileStoreTests.cs` / `FileHashCaptureTests.cs` for the established
pattern). The new collaborator is `WorkspaceScopeEnforcer` in
`src/Guardrails.Core/Execution/WorkspaceScopeEnforcer.cs`. Tests to encode:

- `Snapshot(workspace, writeScope, enforcementIgnore)` walks the workspace, hashing
  the non-ignored tree (SHA-256, content-based — reuse the FileHashCapture primitive).
- Post-action DETECT (no revert in M4): given a pre-image and the current tree, a write
  to a path OUTSIDE the writeScope is reported as an out-of-scope violation; an edit
  INSIDE the writeScope is not; a no-op touch (same bytes) is NOT a violation
  (content-based, not mtime).
- `enforcementIgnore` excludes `state/`, `.git/`, `**/bin/**`, `**/obj/**`,
  `**/node_modules/**` — a write under an ignored path is never a violation.
- Detection is the signal that FAILS the attempt — assert the enforcer reports
  `HasOutOfScopeWrites` (or equivalent) with the offending path(s). No reverting yet.

The tests MUST fail (or fail to compile, because `WorkspaceScopeEnforcer` does not
exist yet) against current code — that is intentional. Do NOT implement the enforcer
or touch TaskExecutor here.

You do NOT need to hash anything or write to state — `captureHashes` handles it.
Publish nothing to state.
