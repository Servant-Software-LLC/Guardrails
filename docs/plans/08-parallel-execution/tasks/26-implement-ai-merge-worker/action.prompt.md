## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 §4 / §9.1's AI-merge worker so `AiMergeWorkerTests` pass:
- New `src/Guardrails.Core/Execution/AiMergeResolver.cs` over the EXISTING `IPromptRunner` (the runner
  contract returns metadata only - there is no byte channel), using the NEW merge env contract:
  `GUARDRAILS_MERGE_BASE`, `GUARDRAILS_MERGE_OURS`, `GUARDRAILS_MERGE_THEIRS` (three-way inputs on disk)
  and `GUARDRAILS_MERGE_OUT` (the worker writes the resolution; the harness reads it). A rationale is
  logged, NON-gating. `PromptResult.IsError` / the exit code is NEVER the verdict.
- A distinct `ai-merge` prompt profile under `promptRunners` (NOT a `guardrailOverrides`-shaped profile):
  `Model/PromptRunnerConfig.cs`.
- The two DETERMINISTIC checks: (i) no conflict markers remain (`git diff --check`); (ii) blast-radius -
  the AI touched ONLY the git-reported-conflicted files (`git status --porcelain`). A violation ⇒ discard
  (`reset --hard`) + needs-human. 1-retry budget; escalate to needs-human on markers/out-of-bounds/
  re-verify-fail/budget.
- Wire AI-merge into the §4 union path (fan-in + non-FF), OFF the global lock in the private worktree,
  with the verdict = the `IReVerifier` re-verify (the colliding siblings' FULL set UNCONDITIONALLY - B-3).

Make `AiMergeWorkerTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Keep the solution building. Publish nothing to state.
