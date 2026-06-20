## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 M2's real git worktree lifecycle so `GitWorktreeLifecycleTests` pass:
- New `src/Guardrails.Core/Execution/GitWorktreeProvider.cs` implementing `IWorktreeProvider` with
  real git: create the plan branch `guardrails/<plan-name>` off the user's HEAD + the integration
  worktree on it; segment worktrees with the reuse topology (root fork off plan-branch tip; linear
  hop REUSES the upstream's segment worktree; fan-out INHERIT-ONE the longest chain + FORK-THE-REST
  off the producer's RECORDED commit sha captured at producer-commit time, NEVER a live rev-parse of
  the segment branch - W-2; fan-in forks one upstream); `Discard` via `git worktree remove --force`;
  `PruneOrphans`. Reuse/topology logic may live in a helper `Execution/WorktreeManager.cs`.
- Point `TaskExecutor`'s child-process cwd at the assigned segment worktree.
- Worktrees are created under a harness-owned root OUTSIDE the workspace (default
  `<temp>/guardrails-worktrees/<workspace-hash>/<runId>/`).

Do NOT yet add validation gates / triad deletion (that is task 08) or integration (M4) - this task is
the worktree lifecycle + reuse topology ONLY. Make `GitWorktreeLifecycleTests` pass WITHOUT editing
them; if they are genuinely wrong, emit `{"needsHuman": "<why>"}`. Keep the solution building.
Publish nothing to state.
