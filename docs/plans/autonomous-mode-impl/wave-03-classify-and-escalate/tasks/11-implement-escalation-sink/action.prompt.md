## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/11-implement-escalation-sink` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the **file-based `IEscalationSink`** (issue #361 Phase 3) by filling REAL logic over the
`FileEscalationSink` stub the previous task authored. The design of record is
`docs/plans/12-autonomous-mode.md` §7.1/§7.2. Make the authored `EscalationSinkTests` pass WITHOUT
editing them.

Implement `FileEscalationSink.Escalate(EscalationRequest)` to (doc 12 §7.2, a thin layer over shipped
machinery — NOT a new transport):
1. **Allocate a durably-MONOTONIC, never-reused `seq`** from a persisted run-level counter (journaled —
   add the counter to `RunJournal`, grep `RunJournal.RecordDecision` / the journal document for where
   run-level state persists; do NOT derive `seq` from a directory listing, §7.1 Finding 5). This is why
   this task also owns `RunJournal.cs`.
2. **Write** `logs/<runId>/escalations/<seq>-<gate>.json` — the serialized `EscalationRequest` + the
   assigned `EscalationId` + `status: "open"` + the `DefinitionHash`. Use the repo's atomic-write helper
   (grep `AtomicFile`).
3. **Append a `decisions[]` entry** via the shipped `RunJournal.RecordDecision` with
   `decision == escalated` and the §6.2 fields (gate, criticality, threshold, assessmentRef).
4. **Emit `IRunObserver.DecisionRecorded`** so a live UI / stdout shows the escalation as it happens.
5. **Never block** — record and return the `EscalationId`; the answer arrives out of band and is consumed
   by a later resume (the answer-consumption task owns that).

Behave like a per-unit `needs-human`: the escalated unit halts and its dependents block; independent
branches keep running (the shipped semantics — the WIRING task connects this to the Scheduler; here just
implement the sink). Do NOT implement answer consumption or the composition-root wiring (separate tasks).

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~EscalationSinkTests"`. Do NOT run
the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/FileEscalationSink.cs`, `src/Guardrails.Core/Execution/IEscalationSink.cs`,
and `src/Guardrails.Core/Journal/RunJournal.cs` (the journaled seq counter only — do NOT change existing
`RecordDecision` semantics). Do NOT edit the authored tests — if a test is genuinely wrong, emit
`{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit fails the write-scope check and
burns a retry).

Completion criteria (your guardrail checks these): `EscalationSinkTests` and the existing journal tests
in `tests/Guardrails.Core.Tests` all pass.
