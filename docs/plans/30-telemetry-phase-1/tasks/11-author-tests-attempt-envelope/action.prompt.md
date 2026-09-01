## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `11-author-tests-attempt-envelope`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "11-author-tests-attempt-envelope": { "someKey": "someValue" } }`.
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

This task authors the failing tests for the first two items of section 3.4 of
`docs/plans/30-telemetry-phase-1.md`: *"turns-used (computed, printed and discarded today), segmented
durations"*. Read section 3.4, and read section 2 — the survivorship finding that reordered the whole
plan — because the failure-path tests below are that finding applied one level down: **a failed
attempt's cost is evidence, and an envelope recorded only on success measures only the runs that
worked.** Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

### One correction to the plan's own pointer, checked while this prompt was written

The plan cites `Scheduler.cs:1908` for the turn count. That line is inside the **JIT wave-breakdown
gate**, whose `NumTurns` is the breakdown invocation's own turn count — a different number about a
different thing. The turn count this task is about dies earlier and elsewhere: at **`ActionRun.FromPrompt`**
in `src/Guardrails.Core/Execution/ActionRunner.cs`, which restates the `PromptResult` for the attempt
loop, copies cost, usage and observed model, and drops `NumTurns` on the floor. The plan remains
authoritative about WHAT to record; this one pointer was verified and aims at the wrong site.

## Task

Author **one** file, and only this one:
`tests/Guardrails.Core.Tests/Execution/AttemptEnvelopeTests.cs`.

It carries **two** `public sealed` classes, each with `[Trait("Category", "ModelEvidence")]` on the
class — the convention every shipped telemetry suite in this project uses (see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs:15`):

- **`AttemptTurnsTests`**
- **`AttemptSegmentsTests`**

Two classes in one file is deliberate and load-bearing: the turn count and the segment durations are
implemented by **two different tasks**, and each one's guardrail filters on its own class alone. A
single class would force each implementation task to assert the other's work was already done, which is
a deadlock the graph cannot see.

Every shape these tests need already exists when this task runs — `AttemptRecord.Turns`,
`AttemptRecord.Segments` / `AttemptSegments` (task 03) and `ActionRun.Turns`, `ActionRun.ActionMs`,
`GuardrailRunResult.GuardrailMs` (task 04). Nothing populates any of them. **These tests must COMPILE
and FAIL at runtime**; not compiling is a mistake to fix, not the intended TDD red. **This task writes
no production file at all** — the carriers are already there.

### How to drive it — the seam, and why this one

Run a **real serial run** and read the journal, using a stub `IPromptRunner` as the only fake.

- `PromptRunnerRegistry.Build(RunConfig config, Func<PromptRunnerConfig, IPromptRunner> factory)` takes
  the runner factory as a parameter. `tests/Guardrails.Core.Tests/Journal/ExecutedDefinitionHashTests.cs`
  is the precedent for the whole fixture — grep its `RunSerialAsync` helper: a temp plan folder, a
  `StateManager`, `RunJournal.LoadOrCreate(plan)`, a `TaskExecutor`, and a `Scheduler` with
  `maxParallelism: 1` and no worktree provider. It passes a factory that throws because every fixture
  action there is a script; **you pass one that returns your stub instead**, and its script-guardrail
  fixture is also the precedent for the failure-path and script-action cases below (note its
  `OperatingSystem.IsWindows()` switch — the fixture scripts must run on Linux and macOS too).
- `IPromptRunner` has exactly two members (`Name`, `RunAsync`), so the stub is a few lines. Have it
  return a `PromptResult` carrying the `NumTurns` the test needs, and have it **await a known minimum
  delay** for the duration tests.

This is deliberate and it is the point: a test that hand-builds an `ActionRun { Turns = 7 }` and calls
a journaller method proves the journaller and says nothing about `FromPrompt`, which is where the
number is dropped today. That is exactly how `AttemptRecord.Usage` shipped structurally dead with every
guardrail green (#475), and `ObservedModelCaptureTests`' own header records the rule: the child process
is faked; the runner interface is where the fake stops.

Assert on the journal document (`RunJournal.Document.Tasks[<id>].Attempts[…]`) — the durable surface a
reader actually strata on.

### The pinned behaviours

Encode **exactly these sixteen**, each as a `[Fact]` with **exactly the method name given**, in the
class named. The names are pinned because this task's guardrail binds each behaviour to its method name
in the runner's TRX; a differently-named test reads as an absent behaviour.

#### class `AttemptTurnsTests`

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | a prompt action's reported turn count reaches the attempt record | `APromptActionsTurnCount_ReachesTheAttemptRecord` |
| 2 | a script action records NO turn count — **null, never 0** | `AScriptAction_RecordsNoTurnCount` |
| 3 | an attempt that FAILED still records its turn count | `AFailedAttempt_StillRecordsItsTurnCount` |
| 4 | a `needs-human` attempt still records its turn count | `ANeedsHumanAttempt_StillRecordsItsTurnCount` |
| 5 | a `permission-denied` attempt still records its turn count | `APermissionWallAttempt_StillRecordsItsTurnCount` |
| 6 | a task-preflight failure records NO turn count — **null, never 0** | `ATaskPreflightFailure_RecordsNoTurnCount` |
| 7 | a MID-ATTEMPT `cancelled` still records its turn count | `AMidAttemptCancel_StillRecordsItsTurnCount` |

#### class `AttemptSegmentsTests`

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 8 | the action phase's elapsed time reaches `AttemptRecord.Segments.ActionMs` | `TheActionsElapsedTime_ReachesTheAttemptSegments` |
| 9 | the guardrail phase's elapsed time reaches `AttemptRecord.Segments.GuardrailMs` | `TheGuardrailsElapsedTime_ReachesTheAttemptSegments` |
| 10 | an attempt that FAILED still records both segments | `AFailedAttempt_StillRecordsItsSegments` |
| 11 | a `needs-human` attempt still records its action segment | `ANeedsHumanAttempt_StillRecordsItsActionSegment` |
| 12 | a `permission-denied` attempt still records its action segment | `APermissionWallAttempt_StillRecordsItsActionSegment` |
| 13 | a structural-wall halt records **BOTH** segments | `AStructuralWallHalt_RecordsBothSegments` |
| 14 | a MID-ATTEMPT `cancelled` still records its action segment | `AMidAttemptCancel_StillRecordsItsActionSegment` |
| 15 | a task-preflight failure records NO segments at all | `ATaskPreflightFailure_RecordsNoSegments` |
| 16 | the PRE-ATTEMPT `cancelled` records NO segments — **null, never a zeroed pair** | `APreAttemptCancel_RecordsNoSegments` |

**Behaviour 2 draws the null-versus-zero line, and it is the same one `TelemetryRow.CostUsd` already
draws.** A script runs no turns; that is *not applicable*, and `0` would be a CLAIM that a model was
invoked and took no turns. Assert `Turns` is null on a script attempt.

**Behaviours 3 and 10 are section 2's survivorship lesson made operative.** Drive a real failure — a
task whose guardrail script exits non-zero, with the retry budget set to 0 — and assert the failed
attempt record still carries its envelope. If the envelope only survives success, the corpus will
report the turn cost of the attempts that worked and nothing about the ones that burned the budget,
which is the exact bias section 2 measured.

### Behaviours 4/5 and 11/12 — the OTHER failure outcomes, and why one per class is not enough

`AttemptJournaler` has **nine** independent `new AttemptRecord` sites, one per outcome, each called
directly from `TaskExecutor`. Nothing funnels through `FailedAttempt`. So a suite that proves the
envelope survives *guardrail-failed* proves nothing about the other seven failure outcomes — and
`needs-human` is not hypothetical: real `run.json` rows in the corpus carry `"outcome":"needs-human"`,
so it is one of the rows a first-pass-rate comparison will actually read.

The two pinned in **this** section are the two that both hold an `ActionRun` at their call site AND
appear in the real corpus; the next section pins two more recorders for a different reason. **Each is
pinned in BOTH classes on purpose:** tasks 12 and 12a filter on their own class alone, so an outcome
pinned only in `AttemptTurnsTests` leaves the same outcome's *segments* unbound, and vice versa. That
asymmetry is exactly the silent half-fix these pins exist to prevent.

Driving them:

- **`needs-human`** — the stub runner writes `{"needsHuman": {"question": "...", "kind":
  "blocked-work"}}` to the state-out path it is handed (read
  `invocation.Environment["GUARDRAILS_STATE_OUT"]`) and returns an ordinary completed `PromptResult`.
  `ActionRunner` parses that fragment into the `NeedsHumanSignal`, and `TaskExecutor` short-circuits to
  `AttemptJournaler.NeedsHuman` before any guardrail runs.
- **`permission-denied`** — the stub returns a `PromptResult` with `BlockedWritePaths` carrying a
  `.claude/` path (a `.claude/` wall is *structural*, which halts on ONE attempt — no repeat needed)
  and a non-success terminal result, so `action.Succeeded` is false. `TaskExecutor` settles via
  `AttemptJournaler.PermissionWall`.

**Assert the OUTCOME as well as the datum.** Each of these four tests must first assert the attempt it
read has `Outcome` `AttemptOutcome.NeedsHuman` (`"needs-human"` on the wire) /
`AttemptOutcome.PermissionDenied` (`"permission-denied"`) respectively, and only then assert the turn
count or the segment. Without that, a fixture that quietly settled some other way would let the test
read a different record — or the wrong attempt — and still look like it proved something.

**Segments on these two paths are HALF-populated, and that is correct.** Both outcomes settle before
any guardrail runs, so `GuardrailMs` is legitimately null there while `ActionMs` is real. Behaviours 11
and 12 therefore assert `Segments` is non-null and `ActionMs` is at least the stub's known delay, and
**must not** assert anything about `GuardrailMs` — an assertion that both members are present on these
paths would be a test demanding a fabricated number.

### Behaviours 7, 13, 14 and 16 — the structural-wall halt and the SPLIT `cancelled` recorder

Two more of the nine recorders are pinned, and they are the two an implementer is most likely to get
half-right — because in both cases the method signature does not tell you the answer.

#### `StructuralWallHalt` (behaviour 13) — the CARRY row with a guardrail half

It takes an `ActionRun` but **no `GuardrailRunResult`**, although its call site holds one: the site is
reached only when guardrails RAN and FAILED and a structural `.claude/` wall coincided that attempt. An
implementation that follows task 12's "read it inside the method" shape therefore ships
`GuardrailMs = null` on a path where the guardrails demonstrably ran — compile-clean, test-green, half
a fix. Behaviour 13 asserts **both** members are present, and carries the same
`GuardrailMs != ActionMs` discrimination behaviour 9 uses; behaviour 13 is the only row besides 9 and
10 that can see a single clock copied into both members on a failure path.

Driving it: the stub returns a **succeeded** `PromptResult` (`Completed = true`, `IsError = false`)
whose `BlockedWritePaths` carries one `.claude/`-rooted path, and the fixture task carries a guardrail
script that exits non-zero. A `.claude/` wall is structural on its FIRST sighting; the action
*succeeded*, so the pure-wall `PermissionWall` site (which requires a failed action) is not reached;
the guardrail failure then routes to `StructuralWallHalt`.

**Its positive control is a COUNT, not an outcome string, and that is the trap in this one.**
`StructuralWallHalt` records outcome `guardrail-failed` — the *same* string the ordinary
guardrail-failed `FailedAttempt` records — so an outcome assertion alone cannot tell the two roads
apart, and a fixture that quietly took the ordinary one would look proven while pinning a recorder this
task never reached. The discriminator is the halt DECISION: this site settles on ONE attempt whatever
the budget. Give the fixture plan `defaultRetries: 2`, then assert the journal recorded **exactly one
attempt**, that the task's status is `needs-human`, and that the attempt's `Outcome` is
`AttemptOutcome.GuardrailFailed` with a non-empty `FailedGuardrails`. The ordinary guardrail-failed road
would have recorded three attempts.

#### `Cancelled` (behaviours 7, 14 and 16) — one method, two answers, decided per call site

Task 12a's own prompt calls this **"the row most likely to be got wrong"**. Two of its three
`TaskExecutor` call sites are mid-attempt — the action returned, so the facts are in hand — and the
third is a PRE-attempt cancel inside the transient-backoff loop, which is handed a *synthesized*
`ProcessResult { Duration = TimeSpan.Zero }` and passes `costUsd: null`. Both halves are pinned,
because binding only the carrying half leaves the fabricated value free: deriving `ActionMs` from that
synthetic `Duration` records a **`0`**, which is strictly worse than a null. `0` is a positive claim
that the action ran and took no time, on a path where no action ran at all — the exact lie this plan
exists to prevent.

**Behaviours 7 and 14 — the MID-ATTEMPT cancel.** Have the stub cancel the run's own
`CancellationTokenSource` immediately before returning an ordinary completed `PromptResult` carrying its
`NumTurns`, and pass that CTS's token to `Scheduler.RunAsync`. The first cancellation check after the
action settles the attempt through `AttemptJournaler.Cancelled` with the action's own facts. Assert the
attempt's `Outcome` is `AttemptOutcome.Cancelled` (`"cancelled"` on the wire) FIRST, then the turn count
/ `Segments.ActionMs`. **This site settles before any guardrail runs**, so — exactly as with behaviours
11 and 12 — assert nothing about `GuardrailMs`. The run itself is cancelled, so do **not** assert
`report.AllSucceeded`; the journal entry is the surface, as it is everywhere else in this file.

**Behaviour 16 — the PRE-ATTEMPT cancel, and the ONE row in this file written at the journaller seam
rather than through a run.** The exception is structural, not a convenience: the pre-attempt site fires
only when cancellation lands in the narrow window between the mid-attempt cancellation check and the
attempt's transient-pause return. A token already cancelled when the next attempt starts settles at the
MID-attempt site instead, so no fixture reaches the pre-attempt site through the scheduler, and a
race-timed fixture would be a flaky guardrail — which teaches an agent to re-run rather than to fix.

So behaviour 16 calls `AttemptJournaler.Cancelled` **directly** (`Guardrails.Core.Tests` has
`InternalsVisibleTo` into `Guardrails.Core`), passing exactly what the pre-attempt call site passes —
the synthesized `new ProcessResult { ExitCode = 0, StandardOutput = "", StandardError = "",
TimedOut = false, Duration = TimeSpan.Zero }` and `costUsd: null`, and nothing else — then reads the
record back off `RunJournal.Document` and asserts `Segments` is **null**. Assert the null on `Segments`
itself, not on its `ActionMs`: an `AttemptSegments` of two nulls would satisfy the latter while still
claiming a measurement was taken and came back empty.

The seam is cheap: `new AttemptJournaler(stateManager, journal)` over a `StateManager` and a
`RunJournal.LoadOrCreate(plan)` you already build for every other test here, and a `TaskNode` off the
loaded plan. Unlike its siblings, `Cancelled` writes no `feedback.md` and creates no log directory, so
the relative-log-dir argument can be any string. Assert the attempt reached
`RunJournal.Document.Tasks[<id>].Attempts` before asserting anything off it — a record that never
landed reads as a null exactly like a correct one.

That is precisely the defect it is aimed at — an implementation that derives the action segment from
the `ProcessResult.Duration` it was handed, INSIDE the method, instead of letting each call site decide.
**Do not generalize this shape.** Every other row in this file goes through a real run; a hand-built
`ActionRun` fed to a journaller method is the hollow shape this task's census names first.

Behaviour 16 asserts a deliberate null, so like behaviours 2, 6 and 15 it is GREEN before task 12a
lands and is a DECLARED EXEMPTION in this task's census (`Expect = 'Executed'`, not `Failed`) — the next
section is where that rule is stated. Behaviours 7, 13 and 14 are ordinary red rows.

#### The two recorders left unbound, named rather than omitted

`RateLimitExhausted` and `NoRoute` are the remaining two of the nine and are deliberately **not**
pinned. Both settle where no action ran at all, so `null` is structurally forced rather than chosen, and
both cost more fixture than they buy: `RateLimitExhausted` needs the whole-task transient-pause budget
exhausted, and `NoRoute` needs a routing configuration whose rung resolves to no candidate. Neither
appears in the census, and the census header says so — an unbound recorder that is written down is a
trade; one that is merely absent is indistinguishable from an oversight.

### Behaviours 6, 15 and 16 — the DELIBERATE NULLS, and why the suite needs them

Three of the nine recorders — `RateLimitExhausted`, `NoRoute` and `TaskPreflightFailed` — plus the
pre-attempt one of `Cancelled`'s three call sites fire where the caller holds no `ActionRun`. There the
honest record is `null` — **never `0`, and never an `AttemptSegments` with both members null**, which
would be a CLAIM that a measurement was taken and came back empty. It is the same null-versus-zero line
`TelemetryRow.CostUsd` draws in its own doc-comment: *"or null when the runner never reported a cost —
NOT the same claim as a recorded `0`."*

Pinning only the carrying half would leave tasks 12 and 12a free to satisfy every green check by
defaulting the uninstructed recorders to `0` or to a zeroed record. Behaviours 6, 15 and 16 bind the
other half, so the implementation cannot silently choose.

`TaskPreflightFailed` is the cleanest one to drive and needs no routing configuration: give the fixture
task a `tasks/<id>/preflights/` check whose script exits non-zero (the same
`OperatingSystem.IsWindows()` script shape the rest of the fixture already uses). The preflight gate
fires *before* the attempt loop, so no action runs at all — the stub runner is never even invoked — and
the journal still records one attempt, with outcome `task-preflight-failed`.

Assert that attempt exists, that its `Outcome` is `AttemptOutcome.TaskPreflightFailed`
(`"task-preflight-failed"` on the wire), and then that `Turns` is null (behaviour 6) / `Segments` is
null (behaviour 15). **Asserting the attempt's existence and outcome
first is what stops these two from being vacuously green**: a fixture whose preflight never fired would
otherwise let a null read as a pass. Behaviour 16 gets the same treatment one seam lower — it asserts
the record actually reached the journal before asserting the null off it.

**All of these are GREEN on today's tree and that is expected** — nothing populates `Turns` or
`Segments` yet, so a *correct* null assertion already holds. Together with behaviour 2 they are the
four declared exemptions in this task's census (`Expect = 'Executed'` rather than `Failed`), and their
job is to STAY green through tasks 12 and 12a. Do not contrive them into failing.

**Duration assertions: lower bounds only.** An upper bound tighter than the attempt's own wall time is
how a duration test flakes on a loaded CI box, and a flaky guardrail teaches an agent to re-run rather
than to fix. Assert:

- `ActionMs` is at least the stub runner's known delay (make the delay comfortably larger than timer
  granularity — a couple of hundred milliseconds);
- `GuardrailMs` is present (not null) on an attempt whose guardrails ran, and is **not equal to**
  `ActionMs` — a single clock copied into both members is the cheapest wrong implementation and the one
  a "both are non-null" test cannot see;
- `ActionMs + GuardrailMs` does not exceed the attempt's own wall time (`EndedAt - StartedAt`) — the
  envelope cannot be larger than the envelope.

Do not assert an exact millisecond value anywhere, and do not assert `GuardrailMs < ActionMs`: spawning
a guardrail process can legitimately outlast a fast action.

**Do NOT implement any of this.** `src/Guardrails.Core/Execution/ActionRunner.cs`,
`GuardrailRunner.cs`, `TaskExecutor.cs` and `AttemptJournaler.cs` are all outside this task's
writeScope; `12-record-the-turn-count` and `12a-segment-the-attempt-durations` own them.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Execution/AttemptEnvelopeTests.cs` — both classes, the stub runner and
every fixture helper live inside that one file. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including changes to production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes
a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
