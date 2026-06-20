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
`tests/Guardrails.Integration.Tests/ResumeAndResetRetryTests.cs` (class name exactly
`ResumeAndResetRetryTests`, selected via `--filter "FullyQualifiedName~ResumeAndResetRetryTests"`).
Encode plan 08 §7 / Stage-2 BEFORE the resume reconciliation + reset-retry exist:
- **resume-after-FF-before-journal:** kill after an FF'd task commit lands but before the journal write;
  resume reads the task succeeded PURELY from the plain commit's trailer (reachable from the plan-branch
  tip), does not re-run, does not double-integrate. Companion: resume-after-fragment-before-commit (the
  B1 reverse window).
- **resume-ignores-stale-segment-ref (W-1):** crash AFTER a segment commit but BEFORE its FF leaves a
  `Guardrails-Task:` trailer on a surviving segment ref that never reached the plan branch; assert
  resume RE-RUNS that task (the stale ref is NOT authoritative), and that `--fresh`/prune
  `git branch -D guardrails/<runId>/*` runs BEFORE any trailer read.
- **retry-preserves-upstream-commits (taskBase ≠ preHead):** a reset-retry in a reused segment worktree
  leaves the upstream's committed file present (asserts the reset targets `<taskBase>`, the task's start
  commit, NOT the plan-branch `preHead`); `git clean -fd` keeps git-ignored build caches and does not
  leave a stale-artifact false-green.

These reference the not-yet-existing resume reconciliation surface, so the project will not compile
against current code - that is the intended "fails on current code" signal. Do NOT implement it - tests
only, in this one file. Publish nothing to state.
