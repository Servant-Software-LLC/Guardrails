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
`tests/Guardrails.Integration.Tests/MergeLockAndSettleTests.cs` (class name exactly
`MergeLockAndSettleTests`, selected via `--filter "FullyQualifiedName~MergeLockAndSettleTests"`).
Encode plan 08 §3 / §5.3 / Stage-2 BEFORE the settle refactor exists. These are the load-bearing
false-green gates - author them precisely:
- **merge-lock-is-net-new:** the integration `SemaphoreSlim(1,1)` serializes two concurrent settles
  into the plan branch; assert it is a DISTINCT lock from the `StateManager`/`RunJournal` `_gate`, and
  that `WorkspaceLock` is gone.
- **FF-integration-is-free + trailer-on-FF-commit:** a linear chain settles via `git merge --ff-only`,
  produces NO merge commit, NO re-verify runs at integration, and each FF'd (plain) commit carries the
  `Guardrails-Task:` / `Guardrails-Run:` trailer.
- **non-FF-union-re-verifies (B1 four-effect):** two siblings settle into the plan branch so the
  second's integration is non-FF and the merged bytes FAIL to build; assert the re-verify FAILS, the
  harness `git reset --hard preHead` (plan-branch HEAD == preHead, no merge commit), the task is
  needs-human, AND the B1 four-effect rollback holds: journaled needs-human (not Succeeded); NO fragment
  in `state.json`; `mergeSequence` NOT consumed; the user branch untouched.
- **settle-ordering (B1 split-brain) — name this test method exactly `Fragment_Written_Before_Commit`:**
  the fixed success order is (1) state-fragment merge → (2) git integration commit → (3) journal
  `Succeeded` + consume `mergeSequence`. Pin that the state fragment is written BEFORE the git
  integration commit (reversing this re-introduces the B1 split-brain), so a journal-before-commit /
  commit-before-fragment implementation is REJECTED. (The scenarios-present guardrail greps for the
  vocabulary `mergeSequence`, `fragment`, a needs-human term, and that exact method name, so the test
  and the guardrail must agree.)

These reference the not-yet-existing settle refactor + the new lock, so the project will not compile
against current code - that is the intended "fails on current code" signal. Do NOT implement the settle
- tests only, in this one file. Publish nothing to state.
