## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/10-author-tests-escalation-sink` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the **file-based `IEscalationSink`** (issue #361 Phase 3,
doc 12 §7.1/§7.2), plus the MINIMAL stubs to compile. The repo uses **xUnit (xunit.v3)**.

**Anchor on REAL shipped types (grep — durable markers):** the sink appends a `decisions[]` entry via
the shipped `RunJournal.RecordDecision` (`src/Guardrails.Core/Journal/RunJournal.cs`) using the extended
`DecisionEntry` (the new optional fields + the `escalated` token landed by task 02), and emits
`IRunObserver.DecisionRecorded` (`src/Guardrails.Core/Execution/IRunObserver.cs`). NOTE: no
`IEscalationSink` / `escalations/` logic exists yet anywhere in the repo — you are authoring it new.

Write these artifacts (all in scope):

1. **The test file** `tests/Guardrails.Core.Tests/EscalationSinkTests.cs` — tests that must FAIL against
   the stubs:
   - **Records to disk**: `Escalate(EscalationRequest)` writes a structured record to
     `logs/<runId>/escalations/<seq>-<gate>.json` (the serialized `EscalationRequest` + the assigned
     `EscalationId(RunId, Seq, Gate, Subject)` + `status: "open"`, carrying the `DefinitionHash`). Assert
     the file exists at that path with those fields. (Use a temp dir for `logs/`.)
   - **Appends a `decisions[]` 'escalated' entry** and **emits `IRunObserver.DecisionRecorded`**: inject
     a fake `IRunObserver` and assert it received a `DecisionRecorded` with `decision == escalated` and
     the §6.2 fields (gate, criticality, threshold).
   - **`seq` is monotonic and never reused**: two escalations in a run get strictly increasing `seq`
     values allocated from a persisted run-level counter (NOT derived from a directory listing) — assert
     `seq2 > seq1`, and that the counter is journaled so it survives a re-read (a later escalation never
     reuses an earlier `seq`).
   - **Never blocks**: `Escalate` returns the assigned `EscalationId` immediately (it does not wait for a
     reply — fire-and-record, §7.1).

2. **The minimal stubs**:
   - `src/Guardrails.Core/Execution/IEscalationSink.cs` — the `IEscalationSink` interface
     (`EscalationId Escalate(EscalationRequest request)`), the `EscalationRequest` record (`Gate`,
     `Subject`, `Question`, `Context`, `Criticality?`, `DefinitionHash`, `At`) and the
     `EscalationId(string RunId, int Seq, string Gate, string Subject)` record, per doc 12 §7.1.
   - `src/Guardrails.Core/Execution/FileEscalationSink.cs` — the `FileEscalationSink` implementation
     whose `Escalate` is a THROWING stub so the tests COMPILE but FAIL (TDD red). Do NOT implement the
     real write / seq allocation / journal append.

   The tests MUST COMPILE and FAIL (not compiling is a mistake). Do NOT touch `RunJournal.cs` here — the
   seq counter is wired by the implementation task; keep the stub self-contained.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~EscalationSinkTests"`. Do NOT run
the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/EscalationSinkTests.cs`,
`src/Guardrails.Core/Execution/IEscalationSink.cs`, and
`src/Guardrails.Core/Execution/FileEscalationSink.cs`. After this task the harness runs a `git diff`
check and rejects any edit outside these paths — including `RunJournal.cs`, `DecisionEntry.cs`, or
`IRunObserver.cs`. An out-of-scope edit fails the task immediately and consumes a retry. If a shipped
type is missing a member you need, do NOT edit it — write `{"needsHuman": "<what is missing>"}` and stop.
