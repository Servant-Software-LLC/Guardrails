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

Encode **exactly these twelve**, each as a `[Fact]` with **exactly the method name given**, in the
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

#### class `AttemptSegmentsTests`

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 7 | the action phase's elapsed time reaches `AttemptRecord.Segments.ActionMs` | `TheActionsElapsedTime_ReachesTheAttemptSegments` |
| 8 | the guardrail phase's elapsed time reaches `AttemptRecord.Segments.GuardrailMs` | `TheGuardrailsElapsedTime_ReachesTheAttemptSegments` |
| 9 | an attempt that FAILED still records both segments | `AFailedAttempt_StillRecordsItsSegments` |
| 10 | a `needs-human` attempt still records its action segment | `ANeedsHumanAttempt_StillRecordsItsActionSegment` |
| 11 | a `permission-denied` attempt still records its action segment | `APermissionWallAttempt_StillRecordsItsActionSegment` |
| 12 | a task-preflight failure records NO segments at all | `ATaskPreflightFailure_RecordsNoSegments` |

**Behaviour 2 draws the null-versus-zero line, and it is the same one `TelemetryRow.CostUsd` already
draws.** A script runs no turns; that is *not applicable*, and `0` would be a CLAIM that a model was
invoked and took no turns. Assert `Turns` is null on a script attempt.

**Behaviours 3 and 9 are section 2's survivorship lesson made operative.** Drive a real failure — a
task whose guardrail script exits non-zero, with the retry budget set to 0 — and assert the failed
attempt record still carries its envelope. If the envelope only survives success, the corpus will
report the turn cost of the attempts that worked and nothing about the ones that burned the budget,
which is the exact bias section 2 measured.

### Behaviours 4/5 and 10/11 — the OTHER failure outcomes, and why one per class is not enough

`AttemptJournaler` has **nine** independent `new AttemptRecord` sites, one per outcome, each called
directly from `TaskExecutor`. Nothing funnels through `FailedAttempt`. So a suite that proves the
envelope survives *guardrail-failed* proves nothing about the other seven failure outcomes — and
`needs-human` is not hypothetical: real `run.json` rows in the corpus carry `"outcome":"needs-human"`,
so it is one of the rows a first-pass-rate comparison will actually read.

The two pinned here are the two that both hold an `ActionRun` at their call site AND appear in the real
corpus. **Each is pinned in BOTH classes on purpose:** tasks 12 and 12a filter on their own class
alone, so an outcome pinned only in `AttemptTurnsTests` leaves the same outcome's *segments* unbound,
and vice versa. That asymmetry is exactly the silent half-fix these pins exist to prevent.

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
any guardrail runs, so `GuardrailMs` is legitimately null there while `ActionMs` is real. Behaviours 10
and 11 therefore assert `Segments` is non-null and `ActionMs` is at least the stub's known delay, and
**must not** assert anything about `GuardrailMs` — an assertion that both members are present on these
paths would be a test demanding a fabricated number.

### Behaviours 6 and 12 — the DELIBERATE NULL, and why the suite needs one

Three of the nine recorders — `RateLimitExhausted`, `NoRoute` and `TaskPreflightFailed` — plus the
pre-attempt one of `Cancelled`'s three call sites fire where the caller holds no `ActionRun`. There the
honest record is `null` — **never `0`, and never an `AttemptSegments` with both members null**, which
would be a CLAIM that a measurement was taken and came back empty. It is the same null-versus-zero line
`TelemetryRow.CostUsd` draws in its own doc-comment: *"or null when the runner never reported a cost —
NOT the same claim as a recorded `0`."*

Pinning only the carrying half would leave tasks 12 and 12a free to satisfy every green check by
defaulting the uninstructed recorders to `0` or to a zeroed record. Behaviours 6 and 12 bind the other
half, so the implementation cannot silently choose.

`TaskPreflightFailed` is the cleanest one to drive and needs no routing configuration: give the fixture
task a `tasks/<id>/preflights/` check whose script exits non-zero (the same
`OperatingSystem.IsWindows()` script shape the rest of the fixture already uses). The preflight gate
fires *before* the attempt loop, so no action runs at all — the stub runner is never even invoked — and
the journal still records one attempt, with outcome `task-preflight-failed`.

Assert that attempt exists, that its `Outcome` is `AttemptOutcome.TaskPreflightFailed`
(`"task-preflight-failed"` on the wire), and then that `Turns` is null (behaviour 6) / `Segments` is
null (behaviour 12). **Asserting the attempt's existence and outcome
first is what stops these two from being vacuously green**: a fixture whose preflight never fired would
otherwise let a null read as a pass.

**Both of these are GREEN on today's tree and that is expected** — nothing populates `Turns` or
`Segments` yet, so a *correct* null assertion already holds. They are declared exemptions in this
task's census (`Expect = 'Executed'` rather than `Failed`), and their job is to STAY green through
tasks 12 and 12a. Do not contrive them into failing.

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
