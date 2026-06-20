## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08's NEW public attempt-decoupled re-verify seam (feasibility-fix-2) so
`ReVerifierSeamTests` pass:
- New `src/Guardrails.Core/Execution/IReVerifier.cs` (public) + a concrete implementation: given a
  worktree path and a guardrail set, run those guardrails against the bytes on disk and return a
  pass/fail result, with NO dependence on an attempt `logDir`, attempt number, or action result. It
  must NOT require or populate `GUARDRAILS_ACTION_STDOUT` / `_STDERR` / `_RESULT`.
- This is distinct from the existing `internal sealed`, attempt-lifecycle-bound `GuardrailRunner`
  (which needs an attempt context); do NOT just re-expose that - build the decoupled seam.

Do NOT yet wire it into FF/union integration or the terminal gate (that is task 18) - this task is the
seam ONLY. Make `ReVerifierSeamTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Keep `src/Guardrails.Core` building. Publish nothing to state.
