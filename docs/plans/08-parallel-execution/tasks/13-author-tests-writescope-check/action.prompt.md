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
- **TDD test-exclusion (the triad replacement) — name this test method exactly `TestFileExcludedFromScope`:**
  an implementation task whose `writeScope` EXCLUDES the test files, editing a test file → the check
  FAILS. (The scenarios-present guardrail greps for that exact method name, so the test and the
  guardrail must agree - a bare "test … scope" keyword is no longer sufficient.)
- A task with **no** `writeScope` runs with **NO** check (the off-switch).
- The check is READ-ONLY in the verdict path: assert it does not itself rewrite tracked files when it
  passes.
- **Scoped-revert keeps in-scope WIP — name this test method exactly `ScopedRevert_KeepsInScopeWip`:**
  after a scope-violating attempt (a diff that touches one IN-scope file AND one OUT-of-scope file),
  the check's scoped revert restores ONLY the out-of-scope file to its `taskBase` content while the
  in-scope file KEEPS its attempt content (the PO's "fix, don't restart"). This pins that an
  over-reverting implementation (e.g. `git checkout <taskBase> -- .`) is REJECTED. The scoped revert is
  implemented by task 14 (which makes this whole `WriteScopeCheckTests` suite pass without editing it),
  so author the test here against the not-yet-existing `WriteScopeCheck`; do NOT gate or omit it. The
  scenarios-present guardrail greps for that exact method name.

These tests reference the not-yet-existing `WriteScopeCheck`, so the project will not compile against
current code - that is the intended "fails on current code" signal. Do NOT implement the check - tests
only, in this one file. Publish nothing to state.
