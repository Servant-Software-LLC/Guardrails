## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/07-implement-blocker-retry` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the **class-(b) blocker bounded wait/backoff** (issue #361 Phase 3) by filling REAL logic over
the `BlockerRetry` stub the previous task authored. The design of record is
`docs/plans/12-autonomous-mode.md` §4.2. Make the authored `BlockerRetryTests` pass WITHOUT editing them.

Implement the loop:
- Retry the blocked attempt with backoff (honoring any parsed reset hint via
  `ClaudeSignalClassifier.ExtractResetHint`) **until** either `AutonomyConfig.BlockerRetry.MaxAttempts`
  OR `TotalWaitSeconds` is reached (whichever first).
- The effective wall-clock bound is **floored by `RunConfig.TransientPauseBudgetSeconds`** (reuse the
  shipped transient-pause discipline — grep `TransientBackoff` / `TransientPauseBudgetSeconds`; do NOT
  re-invent backoff timing).
- On resolution ⇒ return "continue as if the blocker never happened."
- On ceiling ⇒ return "escalate to class-(c)" carrying the full retry ledger (attempts made, cumulative
  wait) so the escalation record can report it (doc 12 §4.2).
- This **never consumes the task's logic-retry budget** (a transient is not a logic failure — the
  shipped rule).
- Drive all waiting through the injectable delay/clock seam the test uses (do NOT call `Task.Delay`
  directly against wall-clock in a way the test cannot control).

Do NOT wire this into the Scheduler here (the composition-root wiring task owns that) and do NOT add the
classifier or assessment. The bounded-wait loop only.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~BlockerRetryTests"`. Do NOT run the
full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/BlockerRetry.cs`. Do
NOT edit the authored tests or the shipped transient types — if a test is genuinely wrong, emit
`{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit fails the write-scope check and
burns a retry).

Completion criteria (your guardrail checks these): `BlockerRetryTests` pass.
