## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/12-author-tests-answer-consumption` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the **v1 answer-file consumption** — the
SECURITY-SENSITIVE reply channel (issue #361 Phase 3, doc 12 §7.4/§7.5/§7.6, DA-7). The repo uses
**xUnit (xunit.v3)**. This is the run's highest-risk surface: an answer file an unattended resume trusts
to proceed past a human gate. Author the FULL matrix — a missing case is a hole.

**Anchor on REAL shipped types (grep — durable markers):** the binding reuses the escalation records
from the sibling escalation-sink task (`EscalationRequest` / `EscalationId` / the `status` lifecycle in
`src/Guardrails.Core/Execution/IEscalationSink.cs` + `FileEscalationSink.cs`) and the shipped definition
hashes `TaskDefinitionHash.Compute` (`src/Guardrails.Core/Journal/TaskDefinitionHash.cs`) +
`WaveDefinitionHash.Compute` (`src/Guardrails.Core/Journal/WaveDefinitionHash.cs`). The injection reuses
`PromptComposer.ComposeAction` (`src/Guardrails.Core/Prompts/PromptComposer.cs`) — the sole prompt
composer; the retry-feedback section is appended by its `AppendPreviousAttempt` helper (a durable marker
to parallel). No answer-file / consumer logic exists yet — you author it new.

Write these artifacts (all in scope):

1. **The test file** `tests/Guardrails.Core.Tests/AnswerFileConsumptionTests.cs` — the security matrix,
   all FAILING against the stub (build escalation records + `…<seq>-<gate>.answer.json` files on a temp
   dir, run consumption, assert the outcome):
   - **Valid answer ⇒ injected once + `status: consumed`**: an answer echoing `{runId, seq, gate,
     subject}` verbatim, with `definitionHash` matching BOTH the escalation record AND the unit's current
     hash, on an answerable gate (`needs-human`) ⇒ the `text` is injected into the next attempt's composed
     prompt as delimited untrusted data, a `decision: answer-injected` (+ provenance `answeredBy`,
     `answerRef`) is recorded, and the escalation `status` flips to `consumed`.
   - **Stale-hash ⇒ reject + re-escalate**: an answer whose `definitionHash` no longer matches the unit's
     CURRENT `TaskDefinitionHash`/`WaveDefinitionHash` (the definition changed since the escalation) is
     rejected and the gate re-escalates (mirrors #274 drift discipline).
   - **Wrong-identity ⇒ reject**: an answer whose `{runId, seq, gate, subject}` does not echo the
     escalation verbatim is rejected.
   - **`seq` never reused across resumes**: a stale unconsumed answer cannot bind a LATER escalation that
     reuses `{runId, gate, subject}` — the monotonic `seq` makes the tuple unique for the run's life.
   - **Once-only + CAS**: a re-dropped answer for an already-`consumed` escalation (even under a NEW
     `runId`) is ignored; two concurrent resumes never double-inject (CAS-guarded status flip, consumption
     persisted in the CREATING run's `escalations/` dir — cross-`runId` bookkeeping).
   - **Review-gate answer ⇒ rejected (Blocker 2, #366)**: there is NO `review-attested` answer kind; an
     answer targeting `review-gate` is rejected and NO review marker is ever written.
   - **Clamped-hard-call answer ⇒ rejected (Blocker 1)**: under `proceed-unreviewed`, an answer targeting
     a clamped `high`/`critical` `needs-human`/`wave-checkpoint` escalation is NON-ANSWERABLE — rejected.
   - **Terminal escalation ⇒ not answerable**: an answer targeting a hard-blocker / terminal-exhaustion
     escalation is rejected.
   - **Injected text cannot reach the verdict surface (Finding 4)**: the injected `text` is wrapped as
     clearly-delimited UNTRUSTED human-answer DATA (not a harness/system instruction) — assert the
     composed prompt wraps it in the untrusted-data envelope (the overwatcher denylist is the backstop).
   - **`wave-checkpoint` answer**: a `wave-proceed` payload (`proceed`/`hold`) applies at the checkpoint.
   - **No answer / malformed answer ⇒ unchanged re-escalate** (graceful degrade; the rejection reason is
     recorded).

2. **The minimal stubs**:
   - `src/Guardrails.Core/Execution/AnswerFile.cs` — the answer-file record (`runId`, `seq`, `gate`,
     `subject`, `definitionHash`, `answeredBy`, `answeredAt`, and the gate-specific `answer` payload
     `{ kind, text }` / `{ kind: wave-proceed, decision }`), per doc 12 §7.4.
   - `src/Guardrails.Core/Execution/AnswerFileConsumer.cs` — the consumer whose consumption method is a
     THROWING stub so the tests COMPILE but FAIL (TDD red). Do NOT implement the real binding/CAS/injection.

   The tests MUST COMPILE and FAIL (not compiling is a mistake). Do NOT modify `PromptComposer.cs` here
   (the implementation task adds the injection section) — the tests may assert on the CONSUMER's decision
   output and, for the injection assertion, on a helper the consumer will expose that the impl task fills.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AnswerFileConsumptionTests"`. Do
NOT run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes
drop `outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/AnswerFileConsumptionTests.cs`,
`src/Guardrails.Core/Execution/AnswerFile.cs`, and
`src/Guardrails.Core/Execution/AnswerFileConsumer.cs`. After this task the harness runs a `git diff`
check and rejects any edit outside these paths — including `PromptComposer.cs`, `IEscalationSink.cs`, or
the definition-hash types. An out-of-scope edit fails the task immediately and consumes a retry. If a
shipped type is missing a member you need, do NOT edit it — write `{"needsHuman": "<what is missing>"}`
and stop.
