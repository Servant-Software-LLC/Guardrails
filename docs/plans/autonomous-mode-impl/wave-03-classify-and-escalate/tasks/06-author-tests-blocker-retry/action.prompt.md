## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/06-author-tests-blocker-retry` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the **class-(b) blocker bounded wait/backoff** (issue #361
Phase 3, doc 12 §4.2), plus the MINIMAL stub to compile. The repo uses **xUnit (xunit.v3)**.

**Anchor on the REAL shipped transient discipline (grep — durable markers):** the bounded-wait REUSES
the shipped transient-pause budget — `RunConfig.TransientPauseBudgetSeconds` (default 14400,
`src/Guardrails.Core/Model/RunConfig.cs`) and `src/Guardrails.Core/Execution/TransientBackoff.cs` (the
cumulative wall-clock pause budget, consumed by `TaskExecutor`). The NEW ceilings come from the shipped
`AutonomyConfig.BlockerRetry` (`MaxAttempts` default 5, `TotalWaitSeconds` default 900, in
`src/Guardrails.Core/Model/AutonomyConfig.cs`). A reset hint is parsed by
`ClaudeSignalClassifier.ExtractResetHint`.

**Design the class to be UNIT-TESTABLE without real waiting:** the new `BlockerRetry` must take an
**injectable clock / delay seam** (an `Func<TimeSpan, Task>` delay, or an `IClock` / `TimeProvider`
abstraction it owns) so a test can assert the backoff timing WITHOUT actually sleeping for 900 seconds.
Author that seam on the new type — the tests inject a fake that records the requested waits.

Write two artifacts (both in scope):

1. **The test file** `tests/Guardrails.Core.Tests/BlockerRetryTests.cs` — tests that must FAIL against
   the stub:
   - **Resolves within the ceiling ⇒ continue**: a transient that clears on attempt K (K < `MaxAttempts`,
     cumulative wait < `TotalWaitSeconds`) returns a "resolved / continue" outcome and records the retry
     ledger (attempts, cumulative wait).
   - **`MaxAttempts` ceiling ⇒ escalate class-(c)**: a transient that never clears reaches `MaxAttempts`
     and returns an "escalate" outcome carrying the ledger (how many attempts, how long waited).
   - **`TotalWaitSeconds` ceiling ⇒ escalate**: cumulative wait hitting `TotalWaitSeconds` first (before
     `MaxAttempts`) also escalates — whichever ceiling trips first.
   - **Floored by `transientPauseBudgetSeconds`**: the effective wall-clock bound is floored by the
     shipped `TransientPauseBudgetSeconds` (assert the two compose as doc 12 §4.2 states — the blocker
     ceiling does not exceed the shipped transient budget floor).
   - **Does not consume the task's retry budget**: a transient is not a logic failure (assert the
     outcome does not decrement a logic-retry counter — the shipped rule).
   - Backoff honors a parsed **reset hint** when present.

2. **The minimal stub**: `src/Guardrails.Core/Execution/BlockerRetry.cs` — a NEW `BlockerRetry` type with
   the injectable delay seam, a result type (resolved | escalate + the retry ledger), and a
   `RunAsync(...)`/`Wait(...)` method that is a THROWING stub so the tests COMPILE but FAIL (TDD red). Do
   NOT implement the real loop.

   The tests MUST COMPILE and FAIL (not compiling is a mistake).

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~BlockerRetryTests"`. Do NOT run the
full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/BlockerRetryTests.cs` and `src/Guardrails.Core/Execution/BlockerRetry.cs`.
After this task the harness runs a `git diff` check and rejects any edit outside these paths — including
`TransientBackoff.cs`/`RunConfig.cs`/`AutonomyConfig.cs` (reference them, do not modify them). An
out-of-scope edit fails the task immediately and consumes a retry. If a shipped type is missing a member
you need, do NOT edit it — write `{"needsHuman": "<what is missing>"}` and stop.
