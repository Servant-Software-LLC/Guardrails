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
`tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs` that encode plan 08 M1's seam BEFORE it
exists. This repo uses **xUnit.v3** (mirror the package versions and test-SDK setup of the existing
`tests/Guardrails.Core.Tests` project — those projects already exist, so copy their conventions).
There is no `[Trait]` convention here; tests are selected by class name via
`--filter "FullyQualifiedName~WorktreeProviderSeamTests"`, so name the class exactly
`WorktreeProviderSeamTests`.

Encode these behaviours (from plan 08 §1 and M1):
- `IWorktreeProvider` exists with the M1-relevant members and `FakeWorktreeProvider` implements it
  (a no-op `Integrate`, and `CreateSegment`/`ReuseSegment` returning a `WorktreeHandle`).
- `WorktreeHandle` carries a worktree path, a segment branch name, its `taskBase` commit, the
  recorded commit sha, and the plan-branch HEAD it descends from; `IntegrationHandle` carries the
  integration worktree path, the plan-branch name, the user's original branch + HEAD, and the runId.
- The executor↔scheduler channel carries a **per-task envelope** (a `TaskNode` together with its
  assigned `WorktreeHandle`), NOT a bare `TaskNode`; `ITaskExecutor.ExecuteAsync` takes the assigned
  `WorktreeHandle` (signature `ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken)`).
- The scheduler can drive 3 independent tasks against a `FakeWorktreeProvider` with overlapping
  execution windows (assert overlap via a gate/barrier, NOT wall-clock timing).

These tests MUST fail against the current code (the types and the new signature do not exist yet) -
that is intentional, not a mistake. The test project will likely not compile against current code;
that is the expected "fails on current code" signal. Do NOT implement the seam or change any
production source - tests only, in this one file. You do NOT need to hash the test file or write to
state - the task's `captureHashes` declaration makes the harness record its hash automatically.
Publish nothing to state.
