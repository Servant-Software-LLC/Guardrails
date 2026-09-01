## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `16-carry-phase1-facts-through-the-worktree-settle`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "16-carry-phase1-facts-through-the-worktree-settle": { "someKey": "someValue" } }`.
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

## Why this task exists

Three upstream tasks have already journalled the Phase-1 attempt facts on the **serial** settle path:

- `06-journal-the-bucket-serial` — the task-fingerprint bucket
- `12-record-the-turn-count` — the attempt's turn count
- `12a-segment-the-attempt-durations` — the action and guardrail segment durations

**Worktree is the DEFAULT execution mode, and it does not use that path.** The worktree settle builds
its OWN `AttemptRecord` and never consults the journaller. So as this task begins, every one of those
three facts reaches serial runs only — and the majority of real runs are worktree runs.

That is not a hypothesis. `src/Guardrails.Core/Journal/JournalModel.cs` documents it in prose (grep for
**`A member hung directly off the attempt record`**) and `src/Guardrails.Core/Execution/RunReport.cs`
carries the worked example (grep for
**`WITHOUT this line the value the record above sets reaches serial runs only`** — the doc comment on
`PendingAttempt.Usage`). `CostUsd` survived that path for exactly one reason: it was declared on
`PendingAttempt`. Its `Usage` sibling did not, until #475 noticed.

This task closes the same gap for the three new facts, in the two places it has to be closed.

## Plan of record

This task implements the worktree half of section 3.2's bucket and section 3.4's turns and segmented
durations, in `docs/plans/30-telemetry-phase-1.md`. Read sections 3.2 and 3.4; where this prompt and
the plan disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

## Two things you do NOT have to carry, and why

`AttemptProvenance.ModelDigest` (task 10) and `AttemptProvenance.RouteWarm` (task 14) already reach
both settle paths **for free**, because `PendingAttempt.Provenance` already exists and
`Scheduler.RecordSucceededSettle` already reads it — grep for `Provenance = pending.Provenance`. That
is the whole reason those two facts were put on the provenance rather than on the record. Do not add
duplicate carriers for them; a second copy of a fact that already arrives is a second thing that can
disagree with the first.

## Site 1: `AttemptJournaler.ValidateFragmentForSettle`

**Authoring-time state — VERIFY IT.** `06-journal-the-bucket-serial`, `12-record-the-turn-count` and
`12a-segment-the-attempt-durations` all edit `src/Guardrails.Core/Execution/AttemptJournaler.cs` before
this task runs. **Grep for the markers; never trust a line number, and re-read what you find.**

Grep for **`new PendingAttempt`**. As authored, the initializer inside `ValidateFragmentForSettle`
already carries `Attempt`, `StartedAt`, `ActionExitCode`, `CostUsd`, `Usage`, `LogDir` and
`Provenance` — and the `Usage` line carries the `#475` comment that is the register to write in.

Add `Bucket`, `Turns` and `Segments` beside them, sourced exactly the way the serial path sources
them (read `CompleteSucceededOrInvalidFragment` — grep for it — and mirror it):

- `ValidateFragmentForSettle` already receives **`ActionRun action`** and
  **`GuardrailRunResult guardrails`** as parameters, so `action.Turns`, `action.ActionMs` and
  `guardrails.GuardrailMs` are all in scope without a new dependency.
- It already receives **`TaskNode task`** as its first parameter, so the bucket is computable there with
  no new dependency and no new field. **Do not take the HOW from this prompt — take it from the file.**
  Grep for `CompleteSucceededOrInvalidFragment`, find the expression `06-journal-the-bucket-serial` hands
  to the recorder for the bucket, and call that same thing here. 06 was free to inline the classifier or
  to extract a small private helper for it, so there is already exactly ONE way to compute a bucket in
  this file; your job is to use it, not to add a second. A second computation site is a second answer,
  and the two can disagree without either one looking wrong.

Each new line gets a doc-style comment in the register `PendingAttempt.Usage`'s already uses: name the
FAILURE the line prevents, not what the line does.

## Site 2: `Scheduler.RecordSucceededSettle`

**Authoring-time state — VERIFY IT.** Grep for **`new Journal.AttemptRecord`** in
`src/Guardrails.Core/Execution/Scheduler.cs`. **Note the `Journal.` qualifier: a bare
`new AttemptRecord` grep misses this site entirely**, and it is the only one of the twelve
`AttemptRecord` construction sites that lives outside `AttemptJournaler.cs` and
`TaskExecutor.RevalidateAsync`.

As authored, the initializer carries `Attempt`, `StartedAt`, `EndedAt`, `ActionExitCode`, `Outcome`,
`CostUsd`, `Usage`, `LogDir` and `Provenance`, every one of them read off `pending`. Then:

- **`Turns` and `Segments` go INTO that initializer**, read off `pending` like their neighbours.
- **`Bucket` does NOT.** `Bucket` is a **TASK-grain** fact — constant across a task's own retries
  within one run — so `03-extend-the-journal-record-shape` declared it on `TaskJournalEntry`, not on
  `AttemptRecord`. Writing `Bucket = pending.Bucket` inside the `new Journal.AttemptRecord { … }`
  initializer will not compile. It travels instead through the recorder call on the next line: grep for
  **`RecordSettleWithAttempt`** and pass **`pending.Bucket`** — the value the journaller already computed
  at Site 1 and carried across the settle boundary for you. **Nothing is computed at this site.**

Whatever `AttemptJournaler.cs` uses to CLASSIFY a bucket stays in `AttemptJournaler.cs`. Do not name that
classifier type anywhere in `Scheduler.cs`: guardrail 03 fails on its bare presence in this file, on
purpose, because a second computation site is a second answer. The scheduler's job here is to carry a
value, not to derive one.

**That call does not compile against the tree as it stands.** Site 3 is the work that makes it compile,
and it is not optional — read it before you write the call.

## Site 3: `ISchedulerJournal` — widen the interface member itself

**This is the piece that makes Site 2's call legal, and the two cheapest ways to get it to compile are
both wrong.** Read the whole section before you touch `Scheduler.cs`.

`Scheduler` holds its journal as an INTERFACE — grep for **`private readonly ISchedulerJournal _journal`**
— so `_journal.RecordSettleWithAttempt(...)` binds against `ISchedulerJournal`, never against
`Journal.RunJournal`. `06-journal-the-bucket-serial` widened `RunJournal`'s public overloads and
deliberately did NOT widen the interface, so
`src/Guardrails.Core/Execution/ISchedulerJournal.cs` still declares `RecordSettleWithAttempt` at its
pre-plan-32 arity with a default body that forwards to `RecordSettle`. Passing one more argument to that
member is a **CS1501** — *"no overload takes N arguments"* — and no amount of re-reading `RunJournal`
changes it.

That is the precedent working as designed, not an obstacle. Grep
**`Widening the interface itself belongs to the task that wires a caller`** in
`src/Guardrails.Core/Journal/RunJournal.cs` and read the doc comment around it: plan 32 left the
interface narrow on purpose and named the future caller as the task that would widen it. **For `bucket`,
this task is that caller.** So widen it here — that is why
`src/Guardrails.Core/Execution/ISchedulerJournal.cs` is in your write-scope.

### What to write

1. **`src/Guardrails.Core/Execution/ISchedulerJournal.cs`** — add `string? bucket = null` as the LAST
   parameter of the **EXISTING** `RecordSettleWithAttempt` member. Leave its default body forwarding to
   `RecordSettle` exactly as it does now — that default is what lets a fake which does not model attempts
   keep working, and the member keeps its default body so a fake that implements nothing still compiles.
   Every existing call site keeps compiling because the parameter is optional. Do not touch
   `RecordSettle`, and do not widen any other member: nothing in this task needs one.

2. **`src/Guardrails.Core/Journal/RunJournal.cs`** — re-arity the EXPLICIT interface implementation
   `void Execution.ISchedulerJournal.RecordSettleWithAttempt(...)` to match, and forward the new
   argument. **This is not optional and it is not cosmetic.** An explicit interface implementation whose
   signature matches no member of the interface is a hard **CS0539** — *"in explicit interface
   declaration is not found among members of the interface that can be implemented"* — so leaving it at
   the old arity does not merely default the bucket to null, it fails guardrail 01's build outright.

   **Forward it by NAME, not by position.** `06-journal-the-bucket-serial` put `bucket` LAST on
   `RunJournal`'s public overload, *after* `definitionHashAtSettle`; the interface member has no
   `definitionHashAtSettle`, so the bucket sits one position earlier there. A positional forward
   therefore lands the bucket in `definitionHashAtSettle` — both are `string?`, so it **compiles
   silently**, and every worktree run then stamps a bucket into the definition-hash field while the
   bucket itself stays null. Read the public overload's real parameter list before you write that call;
   do not write it from memory.

   That forwarder is the ONLY edit this task makes to `RunJournal.cs`. Do not touch its recorder bodies —
   tasks 06, 12 and 12a finished those.

**Check this before you start:** open this task's own `task.json` and confirm
`src/Guardrails.Core/Journal/RunJournal.cs` is in its `writeScope`. If it is not, step 2 cannot be done
in scope and the widening cannot be completed — stop and write
`{"needsHuman": {"question": "widening ISchedulerJournal.RecordSettleWithAttempt makes RunJournal's explicit interface forwarder a CS0539; RunJournal.cs is not in this task's writeScope", "kind": "blocked-work"}}`
to the state-out path. Do not work around it with either of the two moves below.

### Two moves that compile, satisfy every guardrail on this task, and are still wrong

Each of these reaches green today and detonates later. Neither is acceptable, and neither is a matter of
taste.

- **Casting at the call site.** `((Journal.RunJournal)_journal).RecordSettleWithAttempt(…, pending.Bucket)`
  compiles, satisfies guardrail 03's argument-list clause, and passes this task's entire suite — because
  that suite drives the journaller directly and never constructs a `Scheduler`. It then throws
  `InvalidCastException` the first time a `Scheduler` is built over a journal that is not a `RunJournal`,
  which is every fake in the test suite and every future implementation of the seam. You would find out
  at the terminal gate, dozens of tasks downstream, with the cause buried in this one. The interface field
  exists precisely so the scheduler does not know its journal's concrete type; a cast deletes that and
  buys nothing the widening does not give you honestly. **Do not write a cast here.**

- **Adding a SECOND, wider overload to the interface instead of widening the existing member.** Subtler
  and worse. `RunJournal`'s public method matches neither the old member nor the new one, so it
  implements neither, and every real worktree settle would run the interface's own default body instead:
  depending on how you write that body, it drops the attempt record entirely or forwards to the
  narrow member and drops just the bucket. Both are silent, both stay green, and both leave every
  worktree run journalling no bucket. That is exactly the NO-OP-default failure the `RunJournal.cs`
  comment you grepped above exists to warn about. **Widen the member that already exists.**

### Why the forwarder is worth this much prose

`RunJournal.cs`'s explicit forwarder is the reason a Scheduler call reaches the real recorder at all. If
its arity stops short of the bucket, the value defaults back to `null` on the way through: every worktree
run journals no bucket and the corpus report renders `(unbucketed)` for the majority of real runs — the
exact defect section 3.2 exists to close, surviving a fully green run and a fully green suite. Guardrail
03 cannot see it: that check reads `Scheduler.cs` and nothing else. Guardrail 01's build catches the
CS0539. **Nothing catches a bucket forwarded into the wrong parameter** — so before you move on, read the
forwarder you wrote against the public overload's parameter list, argument by argument.

## Guardrail 03 is a source-shape check, and it is one of only two in this plan

`guardrails/03-both-settle-records-set-every-phase1-member.ps1` reads `Scheduler.cs` as TEXT and
asserts that the initializer and the recorder call above really do read `pending`. It exists because
the property is a fact about **two construction sites agreeing**, which no test can observe without
driving the entire scheduler through a real worktree provider. Everything else in this plan was
demoted to a test under the #468 gate; this survived it.

It is the SECOND line of defence. The first is
`tests/Guardrails.Core.Tests/Execution/WorktreeSettlePhase1Tests.cs`, which
`15-author-tests-worktree-settle-carries-phase1` authored and which guardrail 02 runs.

**If guardrail 03 reports something absent that you can see is present, read its message before
escalating.** It strips comments and string literals before matching, so a member named only in a
comment does not satisfy it — that is deliberate, not a defect.

## Do not do these

- **Do NOT edit the tests.** `tests/Guardrails.Core.Tests/Execution/WorktreeSettlePhase1Tests.cs` is
  outside this task's writeScope; an edit there fails the write-scope check and burns a retry. If a
  test is genuinely wrong, write `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the
  state-out path.
- **Do NOT change the serial path.** `CompleteSucceededOrInvalidFragment` and the failure paths were
  finished by tasks 06, 12 and 12a. Read them; do not rewrite them. If the serial path looks wrong to
  you, that is a finding to report in your summary, not a change to make here — it would put this
  task's diff outside what its guardrails certify.
- **Do NOT hang a new member directly off `AttemptRecord` as a shortcut.** That is the exact defect
  this task exists to close, one level down.

## Scope boundary (harness-enforced)

Write only to these four paths, and only for the reason given:

| path | what this task changes there |
|---|---|
| `src/Guardrails.Core/Execution/AttemptJournaler.cs` | Site 1 — three new lines in `ValidateFragmentForSettle`'s `new PendingAttempt` initializer |
| `src/Guardrails.Core/Execution/Scheduler.cs` | Site 2 — `Turns` and `Segments` into `RecordSucceededSettle`'s `new Journal.AttemptRecord` initializer, and `pending.Bucket` into the `RecordSettleWithAttempt` call |
| `src/Guardrails.Core/Execution/ISchedulerJournal.cs` | Site 3 — one optional `string? bucket = null` parameter on the existing `RecordSettleWithAttempt` member |
| `src/Guardrails.Core/Journal/RunJournal.cs` | Site 3 — the explicit `ISchedulerJournal.RecordSettleWithAttempt` forwarder ONLY, re-arity'd to match and forwarding the bucket by name. Nothing else in this file. |

After this task completes, the harness runs a `git diff` check and rejects any edit outside those paths —
including changes to other production files, the authored test file, or the `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry.
