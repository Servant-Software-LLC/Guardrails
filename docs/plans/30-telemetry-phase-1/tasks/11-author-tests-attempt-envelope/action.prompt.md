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

Encode **exactly these six**, each as a `[Fact]` with **exactly the method name given**, in the class
named. The names are pinned because this task's guardrail binds each behaviour to its method name in
the runner's TRX; a differently-named test reads as an absent behaviour.

#### class `AttemptTurnsTests`

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | a prompt action's reported turn count reaches the attempt record | `APromptActionsTurnCount_ReachesTheAttemptRecord` |
| 2 | a script action records NO turn count — **null, never 0** | `AScriptAction_RecordsNoTurnCount` |
| 3 | an attempt that FAILED still records its turn count | `AFailedAttempt_StillRecordsItsTurnCount` |

#### class `AttemptSegmentsTests`

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 4 | the action phase's elapsed time reaches `AttemptRecord.Segments.ActionMs` | `TheActionsElapsedTime_ReachesTheAttemptSegments` |
| 5 | the guardrail phase's elapsed time reaches `AttemptRecord.Segments.GuardrailMs` | `TheGuardrailsElapsedTime_ReachesTheAttemptSegments` |
| 6 | an attempt that FAILED still records both segments | `AFailedAttempt_StillRecordsItsSegments` |

**Behaviour 2 draws the null-versus-zero line, and it is the same one `TelemetryRow.CostUsd` already
draws.** A script runs no turns; that is *not applicable*, and `0` would be a CLAIM that a model was
invoked and took no turns. Assert `Turns` is null on a script attempt.

**Behaviours 3 and 6 are section 2's survivorship lesson made operative.** Drive a real failure — a
task whose guardrail script exits non-zero, with the retry budget set to 0 — and assert the failed
attempt record still carries its envelope. If the envelope only survives success, the corpus will
report the turn cost of the attempts that worked and nothing about the ones that burned the budget,
which is the exact bias section 2 measured.

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
