## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `06-journal-the-bucket-serial`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "06-journal-the-bucket-serial": { "someKey": "someValue" } }`.
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

This task wires the SERIAL half of section 3.2 of `docs/plans/30-telemetry-phase-1.md`: the
task-fingerprint bucket reaching `run.json`. **Read section 3.2's table in full** — it carries the
six rules and their measured distribution over 316 tasks across 18 plan folders. Where this prompt
and the plan disagree, the plan is authoritative and you should say so in your summary.

Two pieces are already on the tree and this task consumes both without changing either:
`Guardrails.Core.Telemetry.TaskFingerprintBucket.Classify(writeScope, guardrails)` (task 02) and
`Journal.TaskJournalEntry.Bucket : string?` (task 03).

**This task is the SERIAL half only.** The worktree settle builds its own `AttemptRecord` from a
`PendingAttempt` and never consults the journaller; carrying the bucket across that boundary is task
`16-carry-phase1-facts-through-the-worktree-settle`. Do not attempt it here —
`src/Guardrails.Core/Execution/Scheduler.cs` is outside this task's writeScope.

## Task

Make `TaskBucketJournalTests` pass by wiring the bucket through two files.

### 1. `src/Guardrails.Core/Journal/RunJournal.cs` — widen the three recorders

Add an optional `string? bucket = null` parameter to each of:

- `RecordAttempt` (line ~235)
- `RecordSettle` (line ~288)
- `RecordSettleWithAttempt` (line ~338)

**Follow the `definitionHash` / `definitionHashAtSettle` precedent already in this file** — it is the
same shape of optional, additive, journal-stamping parameter, added by plan 32 for the same reason,
and it already answers every question you would otherwise have to decide:

- **Position:** last, after `definitionHashAtSettle`, so no existing call site changes.
- **Null-preserves-prior-value semantics:** write it as
  `Bucket = bucket ?? entry.Bucket`, exactly as `DefinitionHash = definitionHash ?? entry.DefinitionHash`
  does at lines 250-253. A call that passes nothing must never CLEAR a bucket a previous call
  recorded — the bucket is task grain and constant across a task's own retries, so the second attempt
  passing null would otherwise erase the first attempt's value.

**Mind the explicit-interface arity forwarders — read the comment at lines 318-329 before you
touch anything.** `Execution.ISchedulerJournal` declares `RecordSettle` and `RecordSettleWithAttempt`
at their pre-plan-32 arities with default-bodied members, and the Scheduler calls them through an
interface-typed field. Adding a parameter to the public overload changes its arity, which stops it
matching the interface member — and without an explicit forwarder every Scheduler call would silently
dispatch to the interface's **NO-OP default** instead of the real implementation. Both forwarders
already exist (`RunJournal.cs:318-329` and the one below `RecordSettleWithAttempt`); adding one more
optional parameter to the public overloads keeps them correct, so **you should need no new forwarder
— verify that both still compile and still forward, and do not delete or re-arity either.** That
silent-no-op is the exact failure mode this file already documents; reproducing it would make the
worktree settle stop journalling entirely while every test here stayed green.

`RecordAttempt` is not on `ISchedulerJournal`, so it has no forwarder concern.

Widening `RecordSettle` and `RecordSettleWithAttempt` is preparatory: nothing calls them with a
bucket yet. That is deliberate — task 16 is the caller, and this task widens all three recorders
together so task 16 touches only `Scheduler.cs` and `AttemptJournaler.cs`.

### 2. `src/Guardrails.Core/Execution/AttemptJournaler.cs` — compute and pass it

**Every `AttemptJournaler` method already takes `TaskNode task` as its first parameter**, so the
bucket is computable at each of them with no new dependency, no new constructor argument and no new
field:

```csharp
TaskFingerprintBucket.Classify(task.WriteScope, task.Guardrails)
```

Pass the result to `_journal.RecordAttempt(...)` at **every** call site in this file — the succeeded
settle in `CompleteSucceededOrInvalidFragment` (line ~91) **and every failure path**. `FailedAttempt`
is the one that carries the failures: `RateLimitExhausted`, `NeedsHuman`, `NoRoute`, `PermissionWall`,
`StructuralWallHalt`, `TaskPreflightFailed` and `Cancelled` route through it or record their own
attempt, and `CompleteSucceededOrInvalidFragment`'s invalid-fragment branch calls `FailedAttempt` too.
Grep this file for `_journal.RecordAttempt(` and cover every hit.

**Why the failure paths are not optional.** Section 2 of the plan measured that every one of 23
failed attempts in plan 27 carried no provenance, so each routed stratum contained only its own
successes and read **100% first-pass — which is not a measurement, it is the definition of what is
left after the failures have been filtered out.** A bucket that lands on successes alone reproduces
that defect at the bucket grain. One of the authored tests
(`FailedAttempt_JournalsTheBucketToo`) exists precisely to catch it.

Where a helper reads better than seven repeated calls, write one — a small private method on
`AttemptJournaler` taking `TaskNode` and returning `string?` is fine. Do not add a constructor
parameter or a cached field: the classifier is a pure static and the task is already in hand.

### Constraints

- **Do NOT change `Classify`'s signature.** It takes exactly
  `(IReadOnlyList<string>? writeScope, IReadOnlyList<GuardrailDefinition> guardrails)` and returns
  `string?`, and a reflection test in `TaskFingerprintBucketTests` pins that. In particular do not add
  a `TaskNode` overload — a parameter list with no task identity in it is what makes reading the
  bucket off the task's name impossible for the compiler to allow, which is the report legend's
  constraint made mechanical.
- **`null` is a legitimate result** — a write surface no rule matches, or a `writeScope` that is null
  (the off-switch, a different claim from a declared `[]`). Pass it through; the corpus reader renders
  it `(unbucketed)`. Do not substitute a sentinel and do not skip the recorder call when it is null.
- **Do NOT edit the authored tests.** Make them pass by fixing the implementation. If a test is
  genuinely wrong or incompatible with the plan's rules, emit
  `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the state-out path rather than
  changing it — an out-of-scope edit to
  `tests/Guardrails.Core.Tests/Journal/TaskBucketJournalTests.cs` fails the write-scope check and
  burns a retry.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Journal/RunJournal.cs` and
`src/Guardrails.Core/Execution/AttemptJournaler.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these two paths — including `Scheduler.cs`,
`TaskExecutor.cs`, `JournalModel.cs`, `TaskFingerprintBucket.cs`, the authored test file, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.
