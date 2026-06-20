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
`tests/Guardrails.Integration.Tests/WriteScopeCheckTests.cs` (class name exactly
`WriteScopeCheckTests`, selected via `--filter "FullyQualifiedName~WriteScopeCheckTests"`; git-touching,
so the integration project). Encode plan 08 §2 / §3.4 BEFORE `Execution/WriteScopeCheck.cs` exists.
Each test sets up a throwaway git worktree with a `taskBase` commit, makes some changes, and runs the
check (`git diff --name-status <taskBase>..<HEAD>` membership against a declared `writeScope`):
- An out-of-scope add/modify/delete → the check FAILS (a guardrail-class failure) with a message naming
  the offending path(s).
- A **rename** presents as a paired **D + A** (no git `-M`); BOTH the old and new path must be in scope,
  else it fails.
- A **deletion**'s path must be in scope.
- **TDD test-exclusion (the triad replacement):** an implementation task whose `writeScope` EXCLUDES the
  test files, editing a test file → the check FAILS.
- A task with **no** `writeScope` runs with **NO** check (the off-switch).
- The check is READ-ONLY in the verdict path: assert it does not itself rewrite tracked files when it
  passes. (The scoped revert on FAILURE is task 14's behaviour; if you assert it here, gate that
  assertion so it only runs once the revert exists, or leave it to task 14's own tests.)

These tests reference the not-yet-existing `WriteScopeCheck`, so the project will not compile against
current code - that is the intended "fails on current code" signal. Do NOT implement the check - tests
only, in this one file. Publish nothing to state.
