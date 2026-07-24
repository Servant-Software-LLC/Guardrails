## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/05-implement-gate-classifier` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the **deterministic gate classifier** (issue #361 Phase 3) by filling REAL logic over the
`GateClassifier` stub the previous task authored. The design of record is
`docs/plans/12-autonomous-mode.md` §4 / §4.1 (the classify-then-act table) and §4.3 (the safe default).
Make the authored `GateClassifierTests` pass WITHOUT editing them.

Implement `GateClassifier.Classify(...)` to map each shipped signal to its class (doc 12 §4.1), keying
on the REAL signal types (grep them, never a line number): `PromptFailureKind.Transient` ⇒
hard-blocker-retryable; `PermissionWallDecision.Halt` / `RunAbort` / preflight failure ⇒
hard-blocker-permanent; agent `needsHuman` / JIT `wave-checkpoint` ⇒ judgment-call; terminal-exhaustion
`needsHuman` / `OverwatchTrigger.NoOpDeadlock` / max-turns / write-scope loop ⇒ floor.

**The load-bearing rule (§4.3, do NOT weaken):** an **UNKNOWN / ambiguous** signal classifies as
**hard-blocker-permanent** (escalate) — NOT retryable. The deterministic classifier is the authority for
the dangerous cases; the safe default is escalate, never spin. This method is a PURE function of its
inputs (no I/O, no prompt) so it stays the unit-test base and is trivially re-runnable.

Do NOT modify the shipped signal types, and do NOT add the criticality assessment or the retry loop here
(separate tasks own those). Classification only.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~GateClassifierTests"`. Do NOT run
the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/GateClassifier.cs`.
Do NOT edit the authored tests or the shipped signal types — if a test is genuinely wrong, emit
`{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit fails the write-scope check and
burns a retry).

Completion criteria (your guardrail checks these): `GateClassifierTests` pass.
