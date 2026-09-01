## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `03-extend-the-journal-record-shape`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "03-extend-the-journal-record-shape": { "someKey": "someValue" } }`.
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

This task lands the JOURNAL half of the schema every later Phase-1 task writes into:
sections **3.2** (the task-fingerprint bucket), **3.3** (the model digest) and **3.4**
(turns-used, segmented durations, warm/cold, machine and concurrency profile, harness and skill
versions) of `docs/plans/30-telemetry-phase-1.md`. **Read those three sections.** Where this prompt
and the plan disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not
work.** Provenance-on-failed-attempts already reaches the journal; you are ADDING members to
`AttemptProvenance`, never re-plumbing how it is recorded.

**Why one task rather than six.** Every Phase-1 datum lands in the same file,
`src/Guardrails.Core/Journal/JournalModel.cs`. Six tasks each widening one record would collide on
it; this task widens it ONCE so the six wiring tasks that follow each touch a different file.

## This is a COLLAPSED TDD pair, and here is the reason

The ordinary split is *author the failing tests* then *implement*. It does not apply to a pure data
model: **the record declaration IS the implementation.** There is no stub-versus-real distinction to
be red about — a property either exists (and the test compiles and passes) or it does not (and the
test does not compile at all). Splitting this would produce an "author-tests" task whose red is a
compile error and an "implement" task whose diff is the property the test already named.

Say plainly in your summary that you understand the consequence: **the anti-tautology protection is
weaker here than in a stub-based pair.** Nothing throws, so a test that constructs an object and
asserts nothing meaningful still passes. That is why every "omitted when null" test below must
serialize through the REAL `JournalJson.Options` and assert on the emitted JSON text — a round-trip
through the real serializer is the only assertion in this file that a hollow test cannot fake.

## Task

Author/extend **two** files, and only these two.

### 1. `src/Guardrails.Core/Journal/JournalModel.cs` — the six members and two new records

All six members are **optional** (nullable) and every one of them MUST carry

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
```

**The attribute is load-bearing, not tidiness.** `JournalJson` sets
`DefaultIgnoreCondition = JsonIgnoreCondition.Never` (`src/Guardrails.Core/Journal/JournalJson.cs:110`),
so a member without it writes `"bucket": null` into EVERY task entry of EVERY run.json, including
runs whose author opted into none of this. The two shapes deserialize identically; the whole cost is
paid by the humans and the tooling reading the file. Every optional member already in this file
carries it — copy that discipline.

| # | member | type | rides | plan |
|---|---|---|---|---|
| 1 | `Bucket` | `string?` | `TaskJournalEntry` (line ~356) | §3.2 |
| 2 | `ModelDigest` | `string?` | `AttemptProvenance` (line ~507) | §3.3 |
| 3 | `RouteWarm` | `bool?` | `AttemptProvenance` | §3.4 |
| 4 | `Turns` | `int?` | `AttemptRecord` (line ~391) | §3.4 |
| 5 | `Segments` | `AttemptSegments?` | `AttemptRecord` | §3.4 |
| 6 | `Environment` | `RunEnvironment?` | `JournalDocument` (line ~10) | §3.4 |

Plus **two new public sealed records** in this same file, beside `AttemptUsage` (line ~455), whose
shape and doc-comment register they should follow:

```csharp
public sealed record AttemptSegments
{
    public long? ActionMs { get; init; }
    public long? GuardrailMs { get; init; }
}

public sealed record RunEnvironment
{
    public string? Host { get; init; }
    public string? Os { get; init; }
    public int? CpuCount { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public int? MaxParallelism { get; init; }
    public string? HarnessVersion { get; init; }
    public string? SkillVersion { get; init; }
}
```

Every member of those two records is nullable for the §15.2 null-versus-zero reason `CostUsd`
already draws: **a runner that reported nothing must not make the journal assert the attempt took no
time.** `0` is a measurement; absent is an absence.

#### The placement of each member is a decision, and each doc comment must record it

- **`Bucket` is TASK grain, not attempt grain.** It is computed from the task's `writeScope` roots
  and its guardrail archetypes, both constant across the task's own retries within one run, so it
  hangs off `TaskJournalEntry` beside `DefinitionHash` rather than being repeated on every attempt.
  Record in the comment that it is *"a fact about a task, never one read off its name"* — the report
  legend §3.2 quotes.
- **`ModelDigest` and `RouteWarm` ride `AttemptProvenance`, and that is mechanical.** Read
  `JournalModel.cs:631-639` — the `Judge` placement note (D32) — before you write either comment. Its
  argument applies verbatim: `AttemptRecord.Provenance` is the only member that already rides
  `PendingAttempt`, and therefore reaches BOTH record-construction paths — the serial
  `AttemptJournaler` AND `Scheduler.RecordSucceededSettle`, which is the DEFAULT worktree mode. **A
  member hung directly off the attempt record lands in serial mode and silently vanishes in worktree
  mode.** Cite that note.
- **`Turns` and `Segments` DO hang directly off `AttemptRecord`, which is the exposed case.** They
  are attempt-grain envelope facts and belong on the record; the consequence is exactly the
  silent-vanish above, which is why task `04-extend-the-transport-record-shape` adds
  `PendingAttempt.Turns`/`PendingAttempt.Segments` carriers and task
  `16-carry-phase1-facts-through-the-worktree-settle` wires them. Say so in the comments — a reader
  who finds `Turns` here and no carrier at the settle must be told where the carrier lives, not left
  to rediscover the defect. `PendingAttempt.Usage`'s comment
  (`src/Guardrails.Core/Execution/RunReport.cs:129-139`) is the worked example of that register.
- **`Environment` is DOCUMENT grain** — probed once per run, identical for every task in it.

#### `ModelDigest`'s doc comment must record the provider reality

This one is not optional prose. The comment MUST state:

- A **Claude row's digest is permanently null**, because the Claude CLI stream carries a model TAG
  and no fingerprint at all — `ClaudeStreamParser` extracts `num_turns`, usage, cost and `model`, and
  nothing else. This is a provider fact, not a gap awaiting a fix.
- An **openai-compat row carries a digest only where the engine volunteers `system_fingerprint`**;
  many do not.
- Therefore **null means "the provider exposed none", never "the harness lost it"** — and the digest
  is a DIFFERENT fact from the model tag beside it: its entire purpose (§3.3) is that a re-quantized
  local model under a stable tag is a different subject and must not be pooled as one sample.

Recording that here is what stops a future reader filing null as a bug. `RequestedModel`
(`JournalModel.cs:542`, #349) is the register to copy: it explains what the field carries, what its
absence MEANS, and why a second field beside `Model` earns its place.

`RouteWarm` is `bool?` and not `bool` for the same class of reason: **"not known" is not "cold"**, and
a script action resolved no route at all.

**One mechanical hazard, stated so you do not trip over it.** Naming a property `Environment` inside
`JournalDocument` shadows `System.Environment` within that record's scope. `JournalModel.cs` uses
`System.Environment` nowhere today (checked: zero hits), so this is safe — but do not "fix" it by
renaming the member, and do not introduce a use of `System.Environment` into that record.

**Do NOT touch any construction site.** This task widens the SHAPE only. Nothing in
`AttemptJournaler.cs`, `Scheduler.cs`, `TaskExecutor.cs`, `TelemetryIngest.cs` or `RunJournal.cs` is
in scope, and every one of them is outside your writeScope.

### 2. `tests/Guardrails.Core.Tests/Journal/Phase1JournalShapeTests.cs` — the seven tests

Class **`Phase1JournalShapeTests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]`
on the class — the convention every shipped telemetry suite in this project uses (see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs:15`).

**Namespace gotcha, and it is real.** Declare `namespace Guardrails.Core.Tests;` — flat, NOT the
nested `Guardrails.Core.Tests.Journal` your folder suggests. Introducing that nested namespace
anywhere in this assembly shadows the production `Guardrails.Core.Journal` namespace for every
unqualified `Journal.X` reference elsewhere in `Guardrails.Core.Tests`. The reason is written out at
the top of `tests/Guardrails.Core.Tests/Journal/JudgeSpendRecordingTests.cs` — read it and follow it.

Encode **exactly these seven behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | `Bucket` rides `TaskJournalEntry`, round-trips a value, and is ABSENT from the JSON when null | `BucketRidesTheTaskEntry_AndIsOmittedWhenNull` |
| 2 | `ModelDigest` rides `AttemptProvenance`, round-trips, and is ABSENT when null | `ModelDigestRidesTheProvenance_AndIsOmittedWhenNull` |
| 3 | `RouteWarm` rides `AttemptProvenance`, round-trips (both `true` and `false`), and is ABSENT when null | `RouteWarmRidesTheProvenance_AndIsOmittedWhenNull` |
| 4 | `Turns` rides `AttemptRecord`, round-trips, and is ABSENT when null | `TurnsRideTheAttemptRecord_AndAreOmittedWhenNull` |
| 5 | `Segments` rides `AttemptRecord`, round-trips both `ActionMs` and `GuardrailMs`, and is ABSENT when null | `SegmentsRideTheAttemptRecord_AndAreOmittedWhenNull` |
| 6 | `Environment` rides `JournalDocument`, round-trips all seven of its members, and is ABSENT when null | `RunEnvironmentRidesTheDocument_AndIsOmittedWhenNull` |
| 7 | one document carrying every Phase-1 member survives serialize → deserialize with every value intact | `EveryPhase1MemberRoundTripsThroughJournalJson` |

**How each "omitted when null" half must be written, and why it is the load-bearing assertion.**
Serialize the object through `JournalJson.Options` — the real one, never a fresh
`JsonSerializerOptions` — and assert the camelCase property name (`"bucket"`, `"modelDigest"`,
`"routeWarm"`, `"turns"`, `"segments"`, `"environment"`) is **ABSENT** from the emitted JSON.
`JsonDocument` + `TryGetProperty` is the idiom `JudgeSpendRecordingTests` already uses; a substring
check on the raw text is acceptable only if it cannot collide with a sibling key.

That assertion is what proves the `[JsonIgnore(Condition = WhenWritingNull)]` attribute is actually
present. Because `JournalJson` sets `DefaultIgnoreCondition = Never`, **forgetting the attribute is
invisible to every other test in this file** — the value still round-trips perfectly, and the only
symptom is `null` noise in every run.json ever written afterwards. This is the one defect in this
task that a property-exists test cannot see.

Pair each with a **positive control**: the same object with the value SET must emit the key. Without
it, an absence assertion passes vacuously against a serializer that emitted nothing at all.

Required members you will have to supply when constructing the fixtures: `JournalDocument` requires
`RunId` and `PlanHash`; `TaskJournalEntry` requires `Status`; `AttemptRecord` requires `Attempt`,
`StartedAt`, `EndedAt`, `Outcome` and `LogDir`.

The tests MUST COMPILE and PASS. This is the collapsed pair: there is no red phase here, and its
guardrail is a FORWARD per-test census that requires each of the seven names to be observed `Passed`.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Journal/JournalModel.cs` and
`tests/Guardrails.Core.Tests/Journal/Phase1JournalShapeTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these two paths — including
`AttemptJournaler.cs`, `Scheduler.cs`, `RunJournal.cs`, `TelemetryIngest.cs`, neighbouring test
files, or either `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If
you hit a compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path
and stop.
