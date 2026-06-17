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
`tests/Guardrails.Core.Tests/ScopeRevertTests.cs` for M5 — enforcement REVERT (§5.3,
§5.4, §10 M5 of `docs/plans/05-disjoint-scope-ownership.md`). Mirror the existing
temp-workspace / git-backed test helpers (see `CapturedFileStoreTests.cs`,
`RunResetTests.cs`). The revert lives on `WorkspaceScopeEnforcer` (a new
`RevertOutOfScope` method) plus the `--fresh`/reset wipe in
`src/Guardrails.Core/State/RunReset.cs`. Tests to encode:

- `RevertOutOfScope(workspace, writeScope, preImage)`:
  - an out-of-scope CREATED file is deleted;
  - an out-of-scope MODIFIED file is restored to pre-attempt bytes;
  - an out-of-scope DELETED file is restored to pre-attempt bytes;
  - IN-scope changes are KEPT (so the next attempt's "fix, don't restart" feedback
    still has the in-scope edits).
- Byte baseline source: a TRACKED out-of-scope file is restored from the git baseline
  (`git checkout -- <path>`); an UNTRACKED out-of-scope file (the #51 case) is restored
  from a lazy byte snapshot under `state/scope-baseline/<path>`.
- The #51 end-to-end shape: a task whose writeScope excludes a test file edits that test
  → attempt 1 FAILS and the revert restores the test → attempt 2 starts CLEAN (pristine
  test) and, with a correct implementation, succeeds.
- `--fresh` / `reset` wipes `state/scope-baseline/` (the `state/captured/` precedent).

The tests MUST fail (or fail to compile, because `RevertOutOfScope` and the
scope-baseline wipe do not exist yet) against current code — that is intentional.
Do NOT implement the revert or the reset wipe here.

You do NOT need to hash anything or write to state — `captureHashes` handles it.
Publish nothing to state.
