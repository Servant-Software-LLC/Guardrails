## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `04-extend-the-transport-record-shape`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04-extend-the-transport-record-shape": { "someKey": "someValue" } }`.
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

This task lands the TRANSPORT half of the schema every later Phase-1 task writes into — the sibling
of `03-extend-the-journal-record-shape`, which added the journal members these carriers must reach.
It serves sections **3.3** (the model digest) and **3.4** (turns-used, segmented durations) of
`docs/plans/30-telemetry-phase-1.md`, plus §3.2's bucket at the worktree settle. **Read §3.3 and
§3.4.** Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not
work.** Provenance already reaches failed attempts; nothing here re-plumbs that.

**Why one task rather than four.** A datum reaches `run.json` only if EVERY hop between the runner
and the journal carries it. Four tasks each widening one hop would collide on `ActionRunner.cs` and
`RunReport.cs`; this task widens all four hops ONCE so the wiring tasks that follow each fill in a
value rather than a member.

## This is a COLLAPSED TDD pair, and here is the reason

Same reason and same declared exemption as `03-extend-the-journal-record-shape`: **a pure data model
has no stub-versus-real distinction.** The record declaration IS the implementation, so there is no
behavioural red to author — a member either exists (the test compiles and passes) or it does not (the
test does not compile at all, which guardrail 01 catches as a compile error). Step 2 criterion (c)
names this as the exemption to the authorship split.

Say plainly in your summary that you understand the consequence: **the anti-tautology protection is
weaker here than in a stub-based pair.** Nothing throws, so a test that constructs an object and
asserts on the value it just set is close to hollow. That is why the fifth test below is a
REFLECTION test over two types rather than a fifth "set it and read it back" — it is the one
assertion in this file that a hollow body cannot satisfy.

## Task

Author/extend **five** files, and only these five.

### 1–4. The four carriers

| # | file | record | member | type |
|---|---|---|---|---|
| 1 | `src/Guardrails.Core/Prompts/PromptInvocation.cs` | `PromptResult` (line ~94) | `ModelDigest` | `string?` |
| 2 | `src/Guardrails.Core/Execution/ActionRunner.cs` | `ActionRun` (line ~384) | `ModelDigest` | `string?` |
| 3 | `src/Guardrails.Core/Execution/ActionRunner.cs` | `ActionRun` | `Turns` | `int?` |
| 4 | `src/Guardrails.Core/Execution/ActionRunner.cs` | `ActionRun` | `ActionMs` | `long?` |
| 5 | `src/Guardrails.Core/Execution/GuardrailRunner.cs` | `GuardrailRunResult` (line ~420) | `GuardrailMs` | `long?` |
| 6 | `src/Guardrails.Core/Execution/RunReport.cs` | `PendingAttempt` (line ~116) | `Turns` | `int?` |
| 7 | `src/Guardrails.Core/Execution/RunReport.cs` | `PendingAttempt` | `Segments` | `Journal.AttemptSegments?` |
| 8 | `src/Guardrails.Core/Execution/RunReport.cs` | `PendingAttempt` | `Bucket` | `string?` |

Every one is **optional** (nullable), init-only, and **defaults to null**. None of these records is
serialized through `JournalJson`, so no `[JsonIgnore]` attribute belongs here — that discipline is
task 03's, on the journal records, and copying it onto these would be cargo.

**Place each member beside its nearest sibling of the same kind**, and say so in the comment:
`ActionRun.ModelDigest` beside `ObservedModel`; `ActionRun.Turns` and `ActionRun.ActionMs` beside
`CostUsd`/`Usage` — all of them "facts an attempt learns from the runner or the clock AFTER launch,
which makes this record their only route to `run.json`", which is the register `ActionRun.Usage` and
`ActionRun.ObservedModel` already use.

`PromptResult.NumTurns` (line ~109) already exists and already carries the runner's turn count —
**do not add a second one.** `ActionRun.Turns` is the hop where it currently DIES:
`ActionRun.FromPrompt` (`ActionRunner.cs:504-539`) copies `CostUsd`, `Usage` and `ObservedModel` and
drops `NumTurns` on the floor. Adding the member is this task; making `FromPrompt` copy into it is
task `12-record-the-turn-count`.

**Do NOT set any of these members at any construction site.** This task widens the SHAPE only:
`FromPrompt`, `FromScript`, `ValidateFragmentForSettle`, `GuardrailRunner.RunAsync` and every other
producer keep exactly the assignments they have today. A member that arrives already populated makes
the later wiring task's tests green before its work lands, which is the false-green this plan exists
to prevent.

#### The three `PendingAttempt` doc comments must cite the failure they prevent

This is the load-bearing prose of the task, and its register is already written in this repo. Read
`PendingAttempt.Usage`'s comment at `src/Guardrails.Core/Execution/RunReport.cs:129-139` and follow
it:

> The worktree settle (`Scheduler.RecordSucceededSettle`) builds its OWN `Journal.AttemptRecord` from
> this object and never consults the journaller — so a value the journaller sets but this record does
> not carry reaches **SERIAL runs only**, and **worktree is the DEFAULT mode**.

That is not a historical anecdote: `#475` shipped the tokens axis once with every guardrail green and
the value present in serial mode alone, and `CostUsd` survives that path for exactly one reason —
it is declared on this record. `Turns`, `Segments` and `Bucket` are the three Phase-1 facts in the
same position, and each comment must say which journal member it is the carrier FOR. Cite
`src/Guardrails.Core/Journal/JournalModel.cs:631-639`, which documents the rule.

`Bucket` is the odd one and its comment must say so: it is TASK grain, so its counterpart at the next
hop is `Journal.TaskJournalEntry.Bucket`, **not** a member of `AttemptRecord`. It rides
`PendingAttempt` because the worktree settle is the only place the scheduler learns anything about
the task at settle time.

### 5. `tests/Guardrails.Core.Tests/Execution/TransportShapeTests.cs` — the five tests

Class **`TransportShapeTests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]` on
the class — the convention every shipped telemetry suite in this project uses (see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs:15`).

Declare `namespace Guardrails.Core.Tests;` — flat, NOT a nested `Guardrails.Core.Tests.Execution`.
Introducing a nested namespace that shadows a production one breaks unqualified `Journal.X`
references elsewhere in this assembly; the reason is written out at the top of
`tests/Guardrails.Core.Tests/Journal/JudgeSpendRecordingTests.cs`.

`ActionRun` and `GuardrailRunResult` are `internal sealed`; `Guardrails.Core.Tests` has
`InternalsVisibleTo` (`src/Guardrails.Core/Guardrails.Core.csproj:27`), so construct them directly.

Encode **exactly these five behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | `PromptResult` carries a `ModelDigest` that round-trips, and defaults to null | `PromptResultCarriesAModelDigest` |
| 2 | `ActionRun` carries `ModelDigest`, `Turns` and `ActionMs`, each round-tripping and each defaulting to null | `ActionRunCarriesTheDigestTurnsAndActionMs` |
| 3 | `GuardrailRunResult` carries `GuardrailMs`, round-tripping and defaulting to null | `GuardrailRunResultCarriesGuardrailMs` |
| 4 | `PendingAttempt` carries `Turns`, `Segments` and `Bucket`, each round-tripping and each defaulting to null | `PendingAttemptCarriesTurnsSegmentsAndBucket` |
| 5 | every Phase-1 carrier on `PendingAttempt` has a counterpart of the same name at the next hop — **by reflection** | `EveryPendingAttemptCarrierHasAnAttemptRecordCounterpart` |

**Behaviour 5 is the point of this file, and it is the trace-the-datum rule made a test.** For each
of `Turns`, `Segments` and `Bucket`, assert that a member of the SAME NAME exists on
`Journal.AttemptRecord` **or** on `Journal.TaskJournalEntry` — either satisfies it. A carrier with no
counterpart at the next hop is a datum that cannot arrive: it would be set at the settle and have
nowhere to be written. `Bucket` lives on `TaskJournalEntry` (task grain), not on `AttemptRecord`, so
a test that demands `AttemptRecord` alone is WRONG and will fail against a correct task 03.

Drive it off a list of the three names rather than three copy-pasted blocks, and **fail with a
message that names which member has no counterpart** — a bare `Assert.True` here tells a retry agent
nothing.

The "defaults to null" half of behaviours 1–4 matters more than it looks: it is what catches a member
declared with an eager default (`= 0`, `= TimeSpan.Zero`, an empty `AttemptSegments`), which would
make every unreported attempt CLAIM a measurement that was never taken — the null-versus-zero rule
§15.2 already draws for cost and tokens.

The tests MUST COMPILE and PASS. This is the collapsed pair: there is no red phase here, and its
guardrail is a FORWARD per-test census that requires each of the five names to be observed `Passed`.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/PromptInvocation.cs`,
`src/Guardrails.Core/Execution/ActionRunner.cs`,
`src/Guardrails.Core/Execution/GuardrailRunner.cs`,
`src/Guardrails.Core/Execution/RunReport.cs` and
`tests/Guardrails.Core.Tests/Execution/TransportShapeTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these five paths — including
`JournalModel.cs` (task 03 owns it), `TaskExecutor.cs`, `AttemptJournaler.cs`, `Scheduler.cs`,
neighbouring test files, or either `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file — most
likely `Journal.AttemptSegments`, which task 03 declares — do NOT edit that file: write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and
stop.
