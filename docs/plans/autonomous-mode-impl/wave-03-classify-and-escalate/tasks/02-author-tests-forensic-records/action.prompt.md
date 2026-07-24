## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/02-author-tests-forensic-records` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the **forensic-record substrate** of autonomous mode
(issue #361 Phase 3, doc 12 §6.1/§6.2/§6.3), plus the MINIMAL stubs to compile. The repo uses **xUnit
(xunit.v3)**; mirror `tests/Guardrails.Core.Tests` — grep for `RunJournal` and its existing tests
(e.g. the journal round-trip tests) for how `state/run.json` and `decisions[]` are serialized
(`RunJournal.RecordDecision` on `src/Guardrails.Core/Journal/RunJournal.cs` appends to the journal
document; `JournalJson.Options` is the serializer). This is the substrate the classifier, blocker-retry,
assessment, and escalation-sink tasks all record THROUGH.

Write these artifacts (all in scope):

1. **The test file** `tests/Guardrails.Core.Tests/AutonomyForensicRecordsTests.cs` — tests that must
   FAIL against the stubs:
   - **DecisionEntry additive fields round-trip**: a `DecisionEntry` carrying the new OPTIONAL fields
     (`gate`, `classification`, `criticality`, `confidence`, `threshold`, `bestGuess`, `blockerAttempts`,
     `blockerWaitedSeconds`, `assessmentRef`, `answerRef`, `answeredBy` — doc 12 §6.2) serializes and
     round-trips through the journal serializer with those fields present, AND a legacy entry WITHOUT them
     still round-trips (the additive/back-compat guarantee — existing `drift`/`task`/`wave` entries
     unchanged).
   - **New decision tokens exist**: the tokens `escalated`, `proceeded-best-guess`, `proceeded-unreviewed`,
     `blocker-retried`, `answer-injected` are available as constants (assert against the constant, not a
     string literal) alongside the shipped `halted`/`prompted-approved`/`prompted-declined`/`auto-applied`.
   - **`autonomy.jsonl` writer appends one record per gate**: `AutonomyJsonl.Append(...)` writes one
     compact single-line JSON object per call to `logs/<runId>/autonomy.jsonl` (append-only — two calls
     ⇒ two lines), with the §6.3 fields (`at`, `gate`, `boundary`, `subject`, `classification`,
     `criticality`, `confidence`, `threshold`, `decision`, plus the gate-specific `question`/`bestGuess`/
     `rationale`). Assert the file has N lines after N appends and each line parses as JSON with the gate
     fields. (Use a temp dir for `logs/`.)

2. **The minimal stubs**:
   - `src/Guardrails.Core/Execution/DecisionEntry.cs` — ADD the new optional fields to the existing
     `DecisionEntry` record (these are DATA — real, nullable/defaulted, additive; do NOT change the
     required `Boundary`/`Policy`/`Decision`/`At`/`Subject`/`Headline`/`Detail` members) and ADD the new
     decision-token constants next to the existing tokens. (This part is real data, not a throwing stub.)
   - `src/Guardrails.Core/Journal/AutonomyJsonl.cs` — a NEW `AutonomyJsonl` type whose `Append(...)`
     method is a THROWING stub (`throw new NotImplementedException();`) with a signature the tests compile
     against. The BEHAVIORAL red lives here: with the fields real and this stub throwing, the test project
     COMPILES (guardrail 01) and the writer tests FAIL against the throwing stub (guardrail 02 = TDD red).

   The tests MUST COMPILE and FAIL (not compiling is a mistake). Do NOT implement the real jsonl writer.

**In-attempt regression check (issue #253 — do NOT skip):** when you run tests to check your red, run
ONLY your own targeted filter — `dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AutonomyForensicRecordsTests"`.
Do NOT run the full unfiltered `dotnet test tests/Guardrails.Integration.Tests`: it contains
fixture-leaking classes (`HarnessWriteRunTests`, `RetrySalvageTests`,
`ScriptActionReproductionShortCircuitTests`, `WriteScopeCheckTests`) that drop `outside.txt` /
`src/output.txt` / `docs/outside.txt` into the worktree and trip this task's write-scope check into a
false-positive rollback (issue #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/AutonomyForensicRecordsTests.cs`,
`src/Guardrails.Core/Execution/DecisionEntry.cs`, and `src/Guardrails.Core/Journal/AutonomyJsonl.cs`.
After this task the harness runs a `git diff` check and rejects any edit outside these paths — including
`RunJournal.cs` or the journal model. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error from a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.
