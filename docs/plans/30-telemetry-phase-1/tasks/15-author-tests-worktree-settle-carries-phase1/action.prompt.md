## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `15-author-tests-worktree-settle-carries-phase1`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "15-author-tests-worktree-settle-carries-phase1": { "someKey": "someValue" } }`.
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

## Why this pair exists

This is the pair that exists because of a failure the codebase has already documented against itself.

`src/Guardrails.Core/Journal/JournalModel.cs` carries it in prose — grep for
**`A member hung directly off the attempt record`** and read the paragraph around it:

> `AttemptRecord.Provenance` is the only member that already rides `PendingAttempt`, and therefore
> reaches BOTH record-construction paths — the serial `AttemptJournaler` AND
> `Scheduler.RecordSucceededSettle`, which is the DEFAULT worktree mode. **A member hung directly off
> the attempt record lands in serial mode and silently vanishes in worktree mode.**

`src/Guardrails.Core/Execution/RunReport.cs` carries the worked example — grep for
**`WITHOUT this line the value the record above sets reaches serial runs only`** and read the doc
comment on `PendingAttempt.Usage` around it. `CostUsd` survived that path for exactly one reason: it
was declared on `PendingAttempt`. Its `Usage` sibling did not, until #475 noticed.

**Worktree is the default execution mode.** So the failure mode this pair guards is not a corner case:
it is the ordinary one. A Phase-1 fact journalled correctly in serial mode and dropped in worktree mode
produces a corpus that is *silently* missing the majority of its rows' data, with a green run and a
green test suite either side of it.

## Plan of record

This task authors the failing tests for the worktree half of section 3.4's items (turns, segmented
durations) and section 3.2's bucket, in `docs/plans/30-telemetry-phase-1.md`. Read sections 3.2 and 3.4;
where this prompt and the plan disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Provenance already reaches both settle paths, and the model digest and route warmth ride it — grep
`Provenance = pending.Provenance` in `src/Guardrails.Core/Execution/Scheduler.cs`. That is why this task
covers only the three members that do NOT ride the provenance.

## What already exists when this task runs

- `03-extend-the-journal-record-shape` has added `AttemptRecord.Turns : int?`,
  `AttemptRecord.Segments : AttemptSegments?` and `TaskJournalEntry.Bucket : string?`.
- `04-extend-the-transport-record-shape` has added `PendingAttempt.Turns : int?`,
  `PendingAttempt.Segments : Journal.AttemptSegments?` and `PendingAttempt.Bucket : string?`, plus
  `ActionRun.Turns`, `ActionRun.ActionMs` and `GuardrailRunResult.GuardrailMs`.

So **these tests COMPILE against the tree they run on.** Nothing SETS any of the three carriers yet —
that is `16-carry-phase1-facts-through-the-worktree-settle`'s job — so a correct test goes RED at
runtime. Compiling is required; being red is the point.

## The two settle paths (authoring-time state — VERIFY IT before you rely on it)

Everything below describes the tree **as it stood when this prompt was written**. Tasks
`06-journal-the-bucket-serial`, `12-record-the-turn-count` and `12a-segment-the-attempt-durations` all
edit `AttemptJournaler.cs` **before** this task runs, so **grep for the member names; never trust a
line number, and re-read what you find before asserting on it.**

`src/Guardrails.Core/Execution/AttemptJournaler.cs` is `internal sealed`, and
`tests/Guardrails.Core.Tests` has `InternalsVisibleTo` — so both entry points below are directly
callable from your tests.

| path | entry point (grep for it) | what it produces |
|---|---|---|
| **SERIAL** success | `AttemptJournaler.CompleteSucceededOrInvalidFragment` | builds an `AttemptRecord` itself and calls `_journal.RecordAttempt(...)`, so the fact lands in `run.json` |
| **WORKTREE** success (the DEFAULT) | `AttemptJournaler.ValidateFragmentForSettle` | builds a `PendingAttempt` and hands it back on `TaskResult.PendingAttempt`; it calls **no** journal method — `Scheduler.RecordSucceededSettle` later turns it into the real `AttemptRecord` |

Read both methods before writing anything. Note that `ValidateFragmentForSettle` already receives
`ActionRun action` and `GuardrailRunResult guardrails` as parameters, so every Phase-1 attempt fact is
in scope at that site without a new dependency — which is exactly why task 16 has no excuse.

Drive the journaller against a **real `RunJournal` over a temp directory** (a `StateManager` over the
same temp plan dir), not a fake: the point of these tests is what actually reaches the record, and a
fake journal would let the assertion be about the test's own scaffolding.

## The tests to author

One file, and only this file:
`tests/Guardrails.Core.Tests/Execution/WorktreeSettlePhase1Tests.cs`.

Class **`WorktreeSettlePhase1Tests`**, `public sealed`, in namespace `Guardrails.Core.Tests.Execution`,
carrying `[Trait("Category", "ModelEvidence")]` on the class — the convention every shipped telemetry
suite in this project uses (see `tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs`).

Encode **exactly these four behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | the `PendingAttempt` the worktree settle is built from carries the task's fingerprint bucket | `TheWorktreePendingAttempt_CarriesTheBucket` |
| 2 | …carries the attempt's turn count | `TheWorktreePendingAttempt_CarriesTheTurnCount` |
| 3 | …carries the action and guardrail segment durations | `TheWorktreePendingAttempt_CarriesTheSegments` |
| 4 | the AGREEMENT test: every Phase-1 attempt member is set on BOTH settle paths, member by member | `EveryPhase1AttemptMemberSetOnTheSerialRecord_IsAlsoSetOnTheWorktreeRecord` |

### Behaviour 4 is the one that carries the plan's real invariant — write it carefully

Drive **both** entry points with the SAME inputs (the same `TaskNode`, the same `ActionRun`, the same
`GuardrailRunResult`), then compare what each produced, member by member.

**Enumerate the members by REFLECTION, not by hardcoding three names.** For each Phase-1 carrier
declared on `PendingAttempt` (`Turns`, `Segments`, `Bucket`), resolve its counterpart at the journal
grain — on `Journal.AttemptRecord` where one exists, otherwise on `Journal.TaskJournalEntry`
(**`Bucket` lives on `TaskJournalEntry`, not on `AttemptRecord`** — it is a TASK-grain fact, constant
across a task's own retries within one run, so the test must look in both places). Then assert, for
each name, that **both** sides carry a non-null value.

Reflection is not decoration here. A Phase-1 member added to `PendingAttempt` by a later plan is
covered by this test the day it is declared, without anyone remembering to extend a list — which is the
difference between an invariant and a snapshot.

**Assert BOTH sides non-null, not an implication.** The name reads like an implication ("everything set
on the serial record is also set on the worktree record"), and the implication form is **vacuously
true on the tree this test runs against**: neither path sets anything yet, so "for every member set on
the serial side…" quantifies over an empty set and the test is GREEN. That is a hollow test wearing the
right name, and this task's guardrail will name it. Write the two-sided assertion: for each Phase-1
member, the serial record carries a value AND the worktree carrier carries a value. It goes red today
for both reasons, and it goes green only when task 16 has genuinely closed the gap on both paths.

State that reasoning in a comment in the file. The next reader will otherwise "simplify" it back to the
implication.

### All four must be RED

There are **no declared exemptions** on this task. Nothing sets `PendingAttempt.Bucket`, `.Turns` or
`.Segments` on this tree, so every honest test here fails. A test that constructs a `PendingAttempt`
itself and asserts something about the object it just built is hollow: it passes today, it passes
forever, and this task's guardrail will name it. Each test must obtain its `PendingAttempt` from
`AttemptJournaler.ValidateFragmentForSettle`.

### Do not test through the Scheduler

`Scheduler.RecordSucceededSettle` is `private` and reaching it means standing up an entire run with a
worktree provider. The property that matters at THIS grain is that the carrier is populated — which is
what `ValidateFragmentForSettle` decides. Task 16 additionally ships a source-shape guardrail
(`03-both-settle-records-set-every-phase1-member.ps1`) asserting that the scheduler's own
`new Journal.AttemptRecord { … }` initializer reads those carriers, because that one property genuinely
cannot be observed without driving the whole scheduler. **These tests are the FIRST line of defence and
that guardrail is the second** — which is the right order, and the reason the guardrail is one of only
two source-shape checks in this entire plan.

**Do NOT implement the carrying.** `src/Guardrails.Core/Execution/AttemptJournaler.cs` and
`src/Guardrails.Core/Execution/Scheduler.cs` are outside this task's writeScope and belong to
`16-carry-phase1-facts-through-the-worktree-settle`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Execution/WorktreeSettlePhase1Tests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path — including changes to
production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and
stop.
