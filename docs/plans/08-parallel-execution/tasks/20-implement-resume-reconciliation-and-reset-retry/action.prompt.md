## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 §7's resume reconciliation + the reset-retry so `ResumeAndResetRetryTests` pass:
- The harness writes the `Guardrails-Task:` / `Guardrails-Run:` trailer on EVERY integrated commit
  (plain FF'd commits AND merge commits). The integrated set on resume = all trailer-bearing commits
  reachable from the **plan-branch TIP** matching the resuming `runId`.
- **W-1:** only trailers reachable from the plan-branch tip are authoritative; a trailer on any surviving
  segment / per-attempt ref that never FF'd is IGNORED. `--fresh`/prune runs
  `git branch -D guardrails/<runId>/*` BEFORE any resume logic reads trailers.
- **reset-retry:** a failed attempt is `git reset --hard <taskBase> + git clean -fd` in its segment
  worktree, where `taskBase` is the task's start commit CAPTURED AT ASSIGNMENT (recorded on the
  `WorktreeHandle`), DISTINCT from the plan-branch `preHead` (conflating them is the corruption bug).
  `-fd` keeps git-ignored build caches.

Make `ResumeAndResetRetryTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Do NOT implement `--merge-on-success` (task 22) or the AI-merge worker (M5).
Keep the solution building. Publish nothing to state.
