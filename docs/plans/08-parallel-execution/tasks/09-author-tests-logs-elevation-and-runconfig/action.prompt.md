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
`tests/Guardrails.Core.Tests/LogsAndRunConfigTests.cs` (class name exactly `LogsAndRunConfigTests`,
selected via `--filter "FullyQualifiedName~LogsAndRunConfigTests"`). Encode plan 08 §8 + §2 +
Decision 5. These tests reference the NOT-YET-EXISTING RunConfig properties (`WorktreeRoot`,
`RunOnCurrentBranch`, `MergeOnSuccess`), so the test project will not compile against current code -
that is the intended "fails on current code" signal (do not work around it):
- Per-attempt log artifacts resolve under `logs/<runId>/<task-id>/attempt-N/` - a top-level sibling
  of `state/`, divided by `runId` - NOT the old `state/logs/<task>/attempt-N/`. `GUARDRAILS_LOG_DIR`
  resolves under `logs/<runId>/...`.
- `RunConfig.MaxParallelism` defaults to **3** when not specified (the worktree-mode default).
- `guardrails.json` accepts `worktreeRoot` (default null), `runOnCurrentBranch` (default false), and
  `mergeOnSuccess` (default false), surfaced on `RunConfig`.

These tests MUST fail against current code (the log path is still under `state/`, the default is 4,
and the new config keys are not surfaced). Do NOT implement the changes - tests only, in this one
file. Publish nothing to state.
