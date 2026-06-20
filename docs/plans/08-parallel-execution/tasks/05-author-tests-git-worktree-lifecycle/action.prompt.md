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
`tests/Guardrails.Integration.Tests/GitWorktreeLifecycleTests.cs` (this is git-touching behaviour, so
it belongs in the integration test project; mirror that project's existing conventions and package
versions). Name the class exactly `GitWorktreeLifecycleTests` (selected via
`--filter "FullyQualifiedName~GitWorktreeLifecycleTests"`). These tests create a throwaway temp git
repo per test and encode plan 08 §1 + M2 BEFORE `GitWorktreeProvider` exists:
- At run start the provider creates a **plan branch** `guardrails/<plan-name>` off the user's current
  HEAD and a harness-owned integration worktree on it; the user's original branch + working tree are
  left untouched.
- A **linear chain** of tasks reuses ONE segment worktree passed along the chain (assert the same
  worktree path is reused for each linear hop - the reuse lever).
- A **fan-out fork-the-rest** sibling, dequeued AFTER the inherit-one successor has advanced the
  shared segment branch, forks off the **producer's RECORDED commit sha**, NOT the inheritor's
  advanced tip (the W-2 gate: assert the forked base equals the producer's recorded sha).
- `Discard`/prune removes a freed worktree; the integration worktree is reattached, not pruned.

These tests MUST fail against the current code (`GitWorktreeProvider` does not exist yet) - that is
intentional. The project will likely not compile against current code; that is the expected signal.
Do NOT implement the provider - tests only, in this one file. You do NOT need to hash the test file or
write to state - `captureHashes` handles it. Publish nothing to state.
