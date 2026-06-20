## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 §3 / §5.3's integration core so `MergeLockAndSettleTests` pass:
- A **net-new `SemaphoreSlim(1,1)` serialize-merges lock** owned by `Scheduler` (there is none today;
  `WorkspaceLock` was deleted). One integration into the plan branch at a time.
- **FF-integration** (linear chain, no sibling advanced the plan branch): `git merge --ff-only`, NO new
  union, NO re-verify; write the `Guardrails-Task:` / `Guardrails-Run:` trailer on the (plain) FF'd
  commit.
- **Non-FF union** (a sibling raced): `git merge --no-commit`, re-verify the merged bytes via the
  `IReVerifier` seam (the union task's own guardrails + the integration-guardrail set), assert porcelain
  shows only the staged merge, then commit with the trailer.
- **The B1 atomic settle under the lock, fixed order:** (1) deep-merge the fragment into `state.json`;
  (2) `git commit` the integration; (3) consume `mergeSequence` + journal `Succeeded`. Every non-success
  path is a single `git reset --hard preHead` (NOT `merge --abort`), leaving state/git/journal unchanged
  and the user checkout untouched; a git/IO failure is a needs-human halt, never an uncaught throw.
- The **terminal `integrationGate`** re-verify runs the integration set on the final HEAD via
  `IReVerifier`.

Do NOT implement resume reconciliation / reset-retry (task 20), `--merge-on-success` (task 22), or the
AI-merge worker (M5) here. Make `MergeLockAndSettleTests` pass WITHOUT editing them; if genuinely wrong,
emit `{"needsHuman": "<why>"}`. Keep the solution building. Publish nothing to state.
