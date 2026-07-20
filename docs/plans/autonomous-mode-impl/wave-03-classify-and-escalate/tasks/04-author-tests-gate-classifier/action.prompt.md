## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/04-author-tests-gate-classifier` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the **deterministic gate classifier** (issue #361 Phase 3,
doc 12 §4 / §4.1 — the classify-then-act table), plus the MINIMAL stub to compile. The repo uses
**xUnit (xunit.v3)**; mirror the shipped classifier tests
`tests/Guardrails.Core.Tests/ClaudeSignalClassifierTests.cs` and
`tests/Guardrails.Core.Tests/PermissionWallTrackerTests.cs` for shape. The classifier is a PURE function
(the ideal unit-test base) — it maps an already-observed signal to a class; it does NOT itself run any
prompt.

**Anchor on these REAL shipped signal types (grep them — durable markers, not line numbers):**
- `PromptFailureKind` (enum in `src/Guardrails.Core/Prompts/PromptFailureKind.cs`) — the `Transient`
  value is the class-(b) retryable signal (`ClaudeSignalClassifier.Classify` /
  `ClaudeSignalClassifier.IsTransient` in `src/Guardrails.Core/Prompts/ClaudeSignalClassifier.cs`).
- `PermissionWallTracker` / `PermissionWallDecision` (`src/Guardrails.Core/Execution/PermissionWallTracker.cs`)
  — the permission-wall signal (class (c), #266).
- `RunAbort` (`src/Guardrails.Core/Execution/RunReport.cs`) — the honest-abort / infra-fault signal
  (class (c), #150).
- `OverwatchTrigger` (`src/Guardrails.Core/Execution/OverwatchTrigger.cs`) — `NoOpDeadlock`,
  `TerminalExhaustion`, etc. are the deterministic FLOOR signals (doc 11 §8).
- The agent-emitted `{"needsHuman": …}` and the JIT `wave-checkpoint` (next wave unauthored) are the
  two dial-eligible JUDGMENT-CALL signals (§4.1).

Write two artifacts (both in scope):

1. **The test file** `tests/Guardrails.Core.Tests/GateClassifierTests.cs` — tests that must FAIL against
   the stub. Assert the classifier maps each signal to the correct class (doc 12 §4.1 table):
   - `PromptFailureKind.Transient` ⇒ **hard-blocker-retryable** (class b).
   - permission wall (`PermissionWallDecision.Halt`) ⇒ **hard-blocker-permanent** (class c).
   - `RunAbort` / infrastructure fault ⇒ **hard-blocker-permanent** (class c).
   - plan/wave **preflight failure** (dependency not materialized) ⇒ **hard-blocker-permanent** (class c).
   - agent `{"needsHuman": …}` (a fresh design question) ⇒ **judgment-call** (class a).
   - JIT **wave-checkpoint** (next wave unauthored) ⇒ **judgment-call** (class a).
   - **terminal-exhaustion** `needsHuman` (a task that could not converge to green) ⇒ **floor** (never
     best-guessed past — invariant 5); no-op-deadlock / max-turns / write-scope loop ⇒ **floor**.
   - an **UNKNOWN / ambiguous** signal ⇒ **hard-blocker-permanent** (class c — the safe default is
     escalate, NOT spin; §4.3). This is the load-bearing negative case.

2. **The minimal stub**: `src/Guardrails.Core/Execution/GateClassifier.cs` — a NEW `GateClassifier` with
   the result enum (`GateClass { JudgmentCall, HardBlockerRetryable, HardBlockerPermanent, Floor }` or
   equivalent) and a `Classify(...)` method that is a THROWING/`default`-returning stub so the tests
   COMPILE but FAIL (TDD red). Do NOT implement the real mapping.

   The tests MUST COMPILE and FAIL (not compiling is a mistake).

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~GateClassifierTests"`. Do NOT run
the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/GateClassifierTests.cs` and
`src/Guardrails.Core/Execution/GateClassifier.cs`. After this task the harness runs a `git diff` check
and rejects any edit outside these paths — including the shipped signal types you reference (do not
modify `PromptFailureKind`/`PermissionWallTracker`/`RunAbort`/`OverwatchTrigger`). An out-of-scope edit
fails the task immediately and consumes a retry. If a shipped signal type is missing a member you need,
do NOT edit it — write `{"needsHuman": "<what is missing>"}` and stop.
