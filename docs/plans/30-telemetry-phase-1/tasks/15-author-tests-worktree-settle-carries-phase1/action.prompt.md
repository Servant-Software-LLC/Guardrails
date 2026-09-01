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

Encode **exactly these five behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | the `PendingAttempt` the worktree settle is built from carries the task's fingerprint bucket | `TheWorktreePendingAttempt_CarriesTheBucket` |
| 2 | …carries the attempt's turn count | `TheWorktreePendingAttempt_CarriesTheTurnCount` |
| 3 | …carries the action and guardrail segment durations | `TheWorktreePendingAttempt_CarriesTheSegments` |
| 4 | the AGREEMENT test: every Phase-1 attempt member is set on BOTH settle paths, member by member | `EveryPhase1AttemptMemberSetOnTheSerialRecord_IsAlsoSetOnTheWorktreeRecord` |
| 5 | the SLOT test: a real worktree settle journals the bucket and the definition hash into their OWN fields | `TheWorktreeSettle_JournalsTheBucketAndTheDefinitionHashInTheirOwnSlots` |

### Behaviour 4 is the one that carries the plan's real invariant — write it carefully

Drive **both** entry points with the SAME inputs (the same `TaskNode`, the same `ActionRun`, the same
`GuardrailRunResult`), then compare what each produced, member by member.

**Name the three carriers explicitly, in the test, as ordinary member access — do NOT enumerate them by
reflection.** The list is `PendingAttempt.Turns`, `PendingAttempt.Segments` and `PendingAttempt.Bucket`,
and each has a counterpart at the journal grain: `Turns` and `Segments` on `Journal.AttemptRecord`, and
**`Bucket` on `Journal.TaskJournalEntry`, NOT on `AttemptRecord`** — it is a TASK-grain fact, constant
across a task's own retries within one run, so the test must look in both places. Assert, for each of
the three, that **both** sides carry a non-null value, in a form whose failure message names the member
(three named pairs, or one loop over three literal rows — either is fine, so long as a failure tells the
reader WHICH member is missing on WHICH side).

**The list is hand-maintained. Say so in the comment, as a fact — do not dress it up as an invariant.**
Nothing in this codebase marks a member as a "Phase-1 carrier": there is no attribute, no marker
interface, no naming convention. So reflection over `PendingAttempt`'s properties cannot tell `Turns`
(a Phase-1 carrier) from `LogDir` or `CostUsd` (not one), and each carrier's counterpart lives on a
different type with no mechanical link back to it — there is nothing to enumerate. A by-name lookup
would also make **absent** and **present but null** indistinguishable: a member renamed out from under
the test would read as an unset value and send the next reader to the wrong file, which is the
hollow-test failure this pair exists to catch. Ordinary member access is bound at compile time and
cannot fail that way — a rename becomes a build error, which is the feedback you want.

So the comment states the truth: these three names are maintained by hand; whoever declares a fourth
Phase-1 carrier adds it here; and what catches a carrier declared with no counterpart is
`03-extend-the-journal-record-shape`'s and `04-extend-the-transport-record-shape`'s shape censuses,
together with sections 3.2 and 3.4 of the plan. **Do not write a comment claiming this test covers
members nobody has declared yet** — it would not be true, and the next reader would trust it.

**Assert BOTH sides non-null, not an implication.** The name reads like an implication ("everything set
on the serial record is also set on the worktree record"), and the implication form is **vacuously
true on the tree this test runs against**: neither path sets anything yet, so "for every member set on
the serial side…" quantifies over an empty set and the test is GREEN. That is a hollow test wearing the
right name, and this task's guardrail will name it. Write the two-sided assertion: for each Phase-1
member, the serial record carries a value AND the worktree carrier carries a value. It goes red today
for both reasons, and it goes green only when task 16 has genuinely closed the gap on both paths.

State that reasoning in a comment in the file. The next reader will otherwise "simplify" it back to the
implication.

### Behaviour 5 is the SLOT test — it is the only one that drives a real Scheduler, and it must

Behaviours 1–4 all stop at the journaller: they prove the carrier is POPULATED. Behaviour 5 proves the
next thing, which nothing else in this plan proves — that the scheduler hands the carried value to the
**right parameter**.

**The defect it pins.** Task 16 widens `ISchedulerJournal.RecordSettleWithAttempt` with a
`string? bucket = null` parameter, landing it **directly beside** the existing `string? definitionHash`.
Two adjacent parameters of the same type mean every confusion between them **compiles**:

```csharp
_journal.RecordSettleWithAttempt(task.Id, record, JournalTaskStatus.Succeeded, mergeSequence, pending.Bucket);
```

That is one argument short, so `pending.Bucket` binds to `definitionHash` and the bucket defaults to
`null`. It costs **two** facts at once, and neither failure is loud:

- the bucket is dropped, so every worktree run's task entry carries none and the corpus report renders
  `(unbucketed)` — the exact §3.2 defect this pair exists to close; **and**
- `TaskJournalEntry.DefinitionHash` is stamped with a **bucket string**. That field is what a resume's
  drift check compares, and what the #322 safe-suffix rewind corroborates a commit's
  `Guardrails-Task-Hash:` trailer against — a trailered commit whose hash is not recorded is **refused**.
  So the damage does not show up as a failure here; it shows up later as a rewind discarding work it
  should have kept.

Task 16's `03-both-settle-records-set-every-phase1-member.ps1` reads `Scheduler.cs` as text. It can see
the shape of the call; it cannot see which field the value landed in. **This test can.**

**The fixture, and it is already in the repo twice — read both before writing.**
`tests/Guardrails.Core.Tests/SchedulerWaveExecutionTests.cs` and
`tests/Guardrails.Core.Tests/Execution/ExecutedDefinitionDivergenceTests.cs` both drive the **real**
`Scheduler` with no git and no processes:

- a plan folder on disk — `WavePlanBuilder` (`internal sealed`, namespace `Guardrails.Core.Tests`, so it
  is visible from `Guardrails.Core.Tests.Execution` without a using) built and `Load()`ed, which is what
  gives each `TaskNode` a real `sha256:`-prefixed `DefinitionHashAtLoad`;
- a real `RunJournal.LoadOrCreate(plan)`;
- `RecordingWorktreeProvider` as the `IWorktreeProvider` — its `Integrate` returns
  `IntegrationResult.FastForward`, which is the branch that calls `RecordSucceededSettle`;
- `observer: IRunObserver.Null`, `reVerifier: null`, `maxParallelism: 4`;
- a fake `ITaskExecutor` returning `Outcome = TaskOutcome.Succeeded` with **`DeferredSettle = true`**.

`Scheduler.RecordSucceededSettle` is `private`, but it is **not unreachable** — `SettleAsync` calls it on
every deferred green settle, which is exactly the path a real worktree run takes. The one thing the two
shipped fixtures do NOT do is carry a `PendingAttempt`: their results leave it null, so the settle takes
the attempt-less `RecordSettle` fallback. **Your result must carry one** — `PendingAttempt` is a
`public sealed record` in `Guardrails.Core.Execution` (`required` members: `Attempt`, `StartedAt`,
`LogDir`) — or the recorder call under test never runs.

**Distinguishable values are the whole point.** Give the `PendingAttempt` a bucket that could never be
mistaken for a hash — `Bucket = "implementation"` — and take the expected definition hash from the
loaded plan itself: `plan.Tasks` is an `IReadOnlyList<TaskNode>`, and the node's `DefinitionHashAtLoad`
is the `sha256:`-prefixed string `SettleAsync` passes down to `RecordSucceededSettle`. Assert it is
non-null and `sha256:`-prefixed first, so a fixture that silently produced no hash cannot make the
comparison below vacuous. Two placeholders that look alike would both still "match" under a slot slip,
and the test would pass while the fields were swapped.

Then assert **both directions**, on the journal entry the run produced — key it off that same
`TaskNode.Id`, which on a waved plan is wave-qualified (`wave-01-scaffold/01-config`), never the bare
folder name (`journal.Document.Tasks[task.Id]`; `journal.RecordedDefinitionHash(task.Id)` reads the same
field):

- `Bucket` is `"implementation"` — and is NOT the definition hash;
- `DefinitionHash` is the task's `DefinitionHashAtLoad` — and is NOT `"implementation"`.

State in a comment that the two-sided form is deliberate: asserting only that each field is non-null, or
only one of the two directions, is satisfied by a swap.

**This test constructs its own `PendingAttempt`, and that does NOT make it hollow** — read the next
section before you conclude otherwise. The hollow shape is asserting about *the object you just built*.
This test asserts about what the **Scheduler** did with it: which journal field the value came out in.
The input is a fixture; the subject is the settle.

### All five must be RED

There are **no declared exemptions** on this task. Nothing sets `PendingAttempt.Bucket`, `.Turns` or
`.Segments` on this tree, and nothing passes a bucket to the recorder, so every honest test here fails.

For behaviours **1–4**: a test that constructs a `PendingAttempt` itself and asserts something about the
object it just built is hollow — it passes today, it passes forever, and this task's guardrail will name
it. Each of those four must obtain its `PendingAttempt` from `AttemptJournaler.ValidateFragmentForSettle`.

Behaviour **5** is the stated exception, for the reason given above: its `PendingAttempt` is the INPUT to
the code under test, not the thing asserted on. It is red today because no bucket reaches the journal
entry at all — `TaskJournalEntry.Bucket` comes back null — and it goes green only when task 16 has
widened the interface and bound the argument by name.

### Where the two lines of defence sit

Behaviours 1–4 are the journaller half: the carrier is populated. Behaviour 5 is the settle half for the
**bucket**: the carried value reaches its own journal field.

What neither can reach is whether `Scheduler.RecordSucceededSettle`'s own
`new Journal.AttemptRecord { … }` initializer READS `Turns` and `Segments` off `pending` rather than
recomputing them — a test sees the value, not where it came from. That residue is task 16's source-shape
guardrail `03-both-settle-records-set-every-phase1-member.ps1`, which is one of only two source-shape
checks in this entire plan. **These tests are the FIRST line of defence and that guardrail is the
second** — which is the right order.

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
