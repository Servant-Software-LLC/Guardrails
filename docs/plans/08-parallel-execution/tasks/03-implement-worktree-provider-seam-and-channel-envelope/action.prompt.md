## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 M1's seam in `src/Guardrails.Core/Execution/` so the upstream
`WorktreeProviderSeamTests` pass:
- New `Execution/IWorktreeProvider.cs` with the M1 members (per the §1 interface sketch:
  `CreateIntegration`, `CreateSegment`, `ReuseSegment`, `ForkFromTip`, `CreateFanIn`, `Integrate`,
  `Discard`, `PruneOrphans`, `MergePlanBranchIntoUserBranch` - stubs are acceptable where a later
  milestone fills them, but the signatures must exist and compile).
- New `Execution/WorktreeHandle.cs` and `Execution/IntegrationHandle.cs` carrying the fields the
  tests assert (worktree path, segment branch name, `taskBase`, recorded sha, plan-branch HEAD;
  integration worktree path, plan-branch name, user original branch + HEAD, runId).
- New `Execution/FakeWorktreeProvider.cs` implementing `IWorktreeProvider` with a no-op `Integrate`.
- Change `ITaskExecutor.ExecuteAsync` to `ExecuteAsync(TaskNode task, WorktreeHandle worktree,
  CancellationToken)` and thread the assigned handle through `TaskExecutor`.
- Replace the bare `Channel.CreateUnbounded<TaskNode>()` in `Scheduler.cs` with a per-task envelope
  carrying the `TaskNode` and its assigned `WorktreeHandle`.

Make `WorktreeProviderSeamTests` pass **without modifying that test file**. If the authored tests are
genuinely wrong or incompatible, emit `{"needsHuman": "<why>"}` rather than editing them. This task
is the seam ONLY - do NOT implement real git worktrees (that is M2), the write-scope check (M3), or
integration (M4). Build the Core project (and the test project) green. Publish nothing to state.
