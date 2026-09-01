## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `05-author-tests-bucket-journaled`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "05-author-tests-bucket-journaled": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task authors the failing tests for the SERIAL half of section 3.2 of
`docs/plans/30-telemetry-phase-1.md` — the bucket reaching `run.json`. **Read section 3.2 in full**,
and read **section 2**, whose finding is the reason behaviour 2 below exists. Where this prompt and
the plan disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not
work.** Provenance already reaches failed attempts.

Two dependencies have already landed, and both matter to how you write this file:

- `02-implement-bucket-classifier` shipped
  `Guardrails.Core.Telemetry.TaskFingerprintBucket.Classify(writeScope, guardrails)` and its six
  `public const string` bucket names.
- `03-extend-the-journal-record-shape` shipped `Journal.TaskJournalEntry.Bucket : string?`.

## The red here is a RUNTIME red, not a compile red — and no stub is needed

Because both dependencies landed, **these tests COMPILE against today's tree.** `Bucket` exists to
read; `Classify` exists to name the expected value. They fail at RUNTIME because **nothing populates
`TaskJournalEntry.Bucket`**: `AttemptJournaler` never computes it and `RunJournal`'s three recorders
have nowhere to put it. That wiring is task `06-journal-the-bucket-serial`, which this task's tests
gate.

So: **do not author a stub.** There is no type to stub — the missing thing is an assignment inside
files this task may not edit. Every one of the five tests below must be RED on today's tree, and each
must be red for the right reason (the journal entry carries `null` where a bucket belongs), not
because it does not compile.

## Task

Author **one** file, and only this one:
`tests/Guardrails.Core.Tests/Journal/TaskBucketJournalTests.cs`.

Class **`TaskBucketJournalTests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]`
on the class — the convention every shipped telemetry suite in this project uses (see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs:15`).

**Namespace gotcha, and it is real.** Declare `namespace Guardrails.Core.Tests;` — flat, NOT the
nested `Guardrails.Core.Tests.Journal` your folder suggests. Introducing that nested namespace
anywhere in this assembly shadows the production `Guardrails.Core.Journal` namespace for every
unqualified `Journal.X` reference elsewhere in `Guardrails.Core.Tests`. The reason is written out at
the top of `tests/Guardrails.Core.Tests/Journal/JudgeSpendRecordingTests.cs` — read it and follow it.

### How to drive the subject

Drive the **real `AttemptJournaler`** against a **real `RunJournal`** over a temp directory, and
assert on the journal DOCUMENT. Do not fake either: the fact under test is that a real settle writes
a real journal entry, and a fake journal would let the wiring task satisfy this file without touching
the code path a run actually takes.

- `AttemptJournaler` is `internal sealed` (`src/Guardrails.Core/Execution/AttemptJournaler.cs:19`);
  `Guardrails.Core.Tests` has `InternalsVisibleTo`
  (`src/Guardrails.Core/Guardrails.Core.csproj:27`). Construct it directly:
  `new AttemptJournaler(stateManager, journal)`.
- `RunJournal`'s constructor is private; the only way in is
  `RunJournal.LoadOrCreate(PlanDefinition plan)`. Build a `PlanDefinition` over a temp directory
  (`PlanDirectory` and `Workspace` both pointing at it), with the `TaskNode`s under test in `Tasks`.
  `PlanDefinition` requires `PlanDirectory`, `Workspace`, `Config` and `Tasks`;
  `tests/Guardrails.Core.Tests/Journal/JudgeSpendRecordingTests.cs:151-202` is the worked example of
  building a minimal `RunConfig` and `TaskNode` — reuse its shape rather than inventing one.
- `new StateManager(plan.PlanDirectory)` then `Initialize()`, as
  `ExecutedDefinitionHashTests.RunSerialAsync` does.
- Read the result from `journal.Document.Tasks[taskId].Bucket`.

The two entry points you need, with the parameters that matter:

- **Succeeded settle:**
  `CompleteSucceededOrInvalidFragment(task, attemptNumber, startedAt, relativeLogDir, logDir, fragmentOutPath, action, guardrails, isFinal, provenance)`.
  Pass a `fragmentOutPath` that does NOT exist — the method only merges when the file is there, so a
  non-existent path takes the plain success path with no `StateManager.MergeFragment` involvement.
- **Failure path:**
  `FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal, outcome, result, …)`.
  It writes `feedback.md` into `logDir`, so create that directory first. `AttemptOutcome.GuardrailFailed`
  is the outcome to use.

`ActionRun` and `GuardrailRunResult` are `internal sealed` too, so construct them directly rather
than reaching for a double.

### The behaviours

Encode **exactly these five behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | a SUCCEEDED serial settle writes the task's bucket onto its journal entry | `SucceededSettle_JournalsTheBucket` |
| 2 | a FAILED attempt journals the bucket too | `FailedAttempt_JournalsTheBucketToo` |
| 3 | two `TaskNode`s with DIFFERENT ids but identical `writeScope` and guardrails journal the SAME bucket | `TheBucketIsComputedFromWriteScopeAndGuardrails_NotFromTheTaskName` |
| 4 | a task declaring `writeScope: []` journals `no-write` | `ATaskThatWritesNothing_JournalsNoWrite` |
| 5 | two attempts of the SAME task journal the same bucket | `TheBucketIsStableAcrossARetryOfTheSameTask` |

**Behaviour 2 is the §2 survivorship lesson applied one level down, and it is not padding.** Section
2 measured that every one of 23 failed attempts in plan 27 carried no provenance, so each routed
stratum contained only its own successes and read 100% first-pass. A bucket that lands on successes
alone reproduces exactly that defect at the bucket grain: `test-authoring` would read as the easy
bucket because its failures were filtered out. **A failure is evidence too.**

### The anti-tautology rule for behaviours 3 and 5 — read this twice

Behaviours 3 and 5 are AGREEMENT tests, and an agreement test is the easiest test in this plan to
write wrong. On today's tree **both journal entries carry `null`**, so

```csharp
Assert.Equal(firstEntry.Bucket, secondEntry.Bucket);   // WRONG - null == null, GREEN today
```

passes against the unwired code and certifies nothing. Its guardrail would report the behaviour as
not-red and you would have burned a retry.

**Every one of the five tests must assert a CONCRETE EXPECTED BUCKET**, spelled as the constant from
`TaskFingerprintBucket` (`TaskFingerprintBucket.Implementation`, `TaskFingerprintBucket.NoWrite`, and
so on) that the fixture's `writeScope` and guardrail names are chosen to produce. For behaviours 3
and 5 assert the concrete value on BOTH entries and then, if you like, their equality on top — never
the equality alone.

Construct each fixture's `writeScope` and `GuardrailDefinition` list so the expected bucket is
unambiguous under §3.2's table: e.g. `writeScope: ["src/**"]` with a guardrail named
`02-something-tests-pass` yields `implementation`; `writeScope: []` yields `no-write`. Read the rules
off the plan's table rather than guessing, and `GuardrailDefinition.Name` is the basename without
extension (`src/Guardrails.Core/Model/GuardrailDefinition.cs`).

For behaviour 3, the two `TaskNode`s must differ in `Id` **and nothing else that the classifier can
see** — same `writeScope` list, same guardrail names. That is what makes it a test of the report
legend's constraint (*"a bucket is a fact about a task, never one read off its name"*) rather than a
test that two objects are equal.

**Do NOT implement the wiring.** The tests MUST COMPILE and FAIL against today's tree — failing is
intentional; not compiling is a mistake to fix. If a test compiles but passes, it is asserting the
wrong thing: it is reading `null` and calling it agreement.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Journal/TaskBucketJournalTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path — including
`src/Guardrails.Core/Execution/AttemptJournaler.cs` and `src/Guardrails.Core/Journal/RunJournal.cs`
(task 06 owns both), any other production file, neighbouring test files, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file — most likely `TaskJournalEntry.Bucket` (task 03) or
`TaskFingerprintBucket` (task 02) — do NOT edit that file: write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and
stop.
