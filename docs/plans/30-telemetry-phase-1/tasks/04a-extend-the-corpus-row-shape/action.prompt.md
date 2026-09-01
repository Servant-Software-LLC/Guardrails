## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `04a-extend-the-corpus-row-shape`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04a-extend-the-corpus-row-shape": { "someKey": "someValue" } }`.
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

This task lands the CORPUS-ROW half of the schema every later Phase-1 task writes into: sections
**3.2** (the task-fingerprint bucket), **3.3** (the model digest) and **3.4** (turns-used, segmented
durations, warm/cold, machine and concurrency profile, harness and skill versions) of
`docs/plans/30-telemetry-phase-1.md`. **Read those three sections.** Where this prompt and the plan
disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

**Why this task exists at all, and it is worth one paragraph.** It is the sibling of
`03-extend-the-journal-record-shape` and `04-extend-the-transport-record-shape`, and it exists for the
same reason plus one more. The shared reason: every Phase-1 datum lands in one file, and one task
widening it once means the wiring tasks that follow each touch a different file. The extra reason is
specific to a TEST project. `tests/Guardrails.Core.Tests` is compiled as a whole, so a test file that
names a column `TelemetryRow` does not have yet does not fail *that test* — it fails the **project**,
for every sibling task whose segment is based on a tree where the test landed and the column had not.
Landing the shape first is what keeps `19-author-tests-row-carries-phase1-facts` an ordinary
author-tests task whose red is a RUNTIME red, like every other one in this plan.

## This is a COLLAPSED TDD pair, and here is the reason

The ordinary split is *author the failing tests* then *implement*. It does not apply to a pure data
model: **the record declaration IS the implementation.** There is no stub-versus-real distinction to be
red about — a property either exists (and the test compiles and passes) or it does not (and the test
does not compile at all, which guardrail 01 catches as a compile error). Splitting this would produce an
"author-tests" task whose red is a compile error — which is precisely the shape this task was created to
remove from the plan.

Say plainly in your summary that you understand the consequence: **the anti-tautology protection is
weaker here than in a stub-based pair.** Nothing throws, so a test that constructs an object and asserts
nothing meaningful still passes. That is why three of the six tests below must go through the REAL
`TelemetryCorpusStore.JsonOptions` or through REFLECTION over the declaration — neither is something a
hollow body can fake.

## Task

Author/extend **two** files, and only these two.

### 1. `src/Guardrails.Core/Telemetry/TelemetryRow.cs` — thirteen columns and a version bump

Add these thirteen, following the file's existing member style exactly — one `///` summary each,
`{ get; init; }`, placed after `Repo` (line 73):

| column | type | wire name | section |
|---|---|---|---|
| `Bucket` | `string?` | `bucket` | 3.2 — the task-fingerprint bucket |
| `ModelDigest` | `string?` | `modelDigest` | 3.3 |
| `Turns` | `int?` | `turns` | 3.4 |
| `ActionMs` | `long?` | `actionMs` | 3.4 — the action phase's wall time |
| `GuardrailMs` | `long?` | `guardrailMs` | 3.4 — the guardrail phase's wall time |
| `RouteWarm` | `bool?` | `routeWarm` | 3.4 — warm/cold |
| `Host` | `string?` | `host` | 3.4 — machine profile |
| `Os` | `string?` | `os` | 3.4 |
| `CpuCount` | `int?` | `cpuCount` | 3.4 |
| `TotalMemoryBytes` | `long?` | `totalMemoryBytes` | 3.4 — unified memory on Apple silicon |
| `MaxParallelism` | `int?` | `maxParallelism` | 3.4 — effective concurrency |
| `HarnessVersion` | `string?` | `harnessVersion` | 3.4 |
| `SkillVersion` | `string?` | `skillVersion` | 3.4 |

The wire names are what `TelemetryCorpusStore.JsonOptions`' `JsonNamingPolicy.CamelCase` produces; you
do not write them, but the tests assert on them.

**Bump `CurrentSchemaVersion` to `2`.** Its own doc comment (line 15) says *"Bump whenever a field is
added, renamed, or reinterpreted."* Thirteen are being added.

Four constraints, each load-bearing:

- **NONE of them may be `required`.** The corpus is append-only and never rewritten, so a v1 line
  written months ago must still deserialise into today's record. `System.Text.Json` gives that for free
  by ignoring absent members — but a `required` member that is absent from the JSON **throws**, which
  would make every historical row unreadable and take the report down with it. `Repo` and the other
  five `required` members were required from the beginning; nothing added now can be.
- **Every value-typed column is `Nullable<T>`** — `int?`, `long?`, `bool?`, never `int`/`long`/`bool`.
  This is §15.2's null-versus-zero rule and it is the whole reason the type column above is worth
  reading twice: a plain `int Turns` defaults to **0**, and the corpus would then assert that an attempt
  nobody measured ran zero turns. `CostUsd`'s doc comment (lines 57-63) already draws the distinction —
  *null* means the runner never reported it, which is **not the same claim** as a recorded `0`. Copy
  that register onto `Turns`, `ActionMs`, `GuardrailMs`, `CpuCount`, `TotalMemoryBytes` and
  `MaxParallelism`. On `RouteWarm`, null is *"no route resolved at all"* (a script action), which is not
  `false` (*"the route was cold"*).
- **Do NOT add `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` to any of them**, and do
  not touch the store's options. This is the one place where this task deliberately diverges from
  `03-extend-the-journal-record-shape`, so do not transfer that habit here. The two files have opposite
  serialization policies and opposite right answers:
  - `JournalJson` sets `DefaultIgnoreCondition = Never` and `run.json` is a document a human reads, so
    every optional journal member carries the attribute to keep null noise out of it.
  - `TelemetryCorpusStore.JsonOptions` sets **no** ignore condition (only camelCase + case-insensitive),
    so a corpus line already writes `"model": null` today for a script attempt. **The stable key set is
    the feature.** The corpus is JSONL that a dataframe reader loads column-wise, and — more sharply —
    it is what makes the schema-version bump mean something: a v2 row ALWAYS carries a `bucket` key, so
    a line where the key is **absent** is a v1 row and a line where it is `null` is a v2 row that had no
    bucket. Omitting nulls would collapse those two into one indistinguishable shape and undo the bump
    you just made.
- **`Bucket`'s doc comment must record what it is a fact ABOUT**: the task's write surface and gate
  shape, derived from `writeScope` and the guardrail archetypes, and **never read off the task's name**.
  §3.2 quotes the report's own legend on that, and `TaskFingerprintBucket.Classify`'s signature (task
  02) already makes the alternative impossible for the compiler to allow.
- **`ModelDigest`'s doc comment must record the provider reality.** A **Claude row's digest is
  permanently null**, because the Claude CLI stream carries a model TAG and no fingerprint at all —
  `ClaudeStreamParser` extracts `num_turns`, usage, cost and `model`, nothing else. An `openai-compat`
  row carries a digest only where the engine volunteers `system_fingerprint`; many do not. So **null
  means "the provider exposed none", never "the harness lost it"** — and the digest is a DIFFERENT fact
  from the model tag beside it: its entire purpose (§3.3) is that a re-quantized local model under a
  stable tag is a different subject and must not be pooled as one sample. Recording that here is what
  stops a future reader filing null as a bug.

**Do NOT touch any construction site.** This task widens the SHAPE only.
`src/Guardrails.Core/Telemetry/TelemetryIngest.cs` maps journal facts onto these columns and is
`20-carry-phase1-facts-into-the-corpus-row`'s job; `TelemetryCommand.cs` renders them and is task 22's.
Both are outside your writeScope.

### 2. `tests/Guardrails.Core.Tests/Telemetry/CorpusRowShapeTests.cs` — the six tests

Class **`CorpusRowShapeTests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]` on the
class — the convention every shipped telemetry suite in this project uses (see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryIngestTests.cs:31`).

Declare `namespace Guardrails.Core.Tests.Telemetry;` — the namespace the shipped telemetry suites in
this folder already use. (The flat-namespace warning at the top of
`tests/Guardrails.Core.Tests/Journal/JudgeSpendRecordingTests.cs` is about the `Journal` folder
shadowing `Guardrails.Core.Journal`; it does not apply here, and this file needs no `Journal` type.)

`TelemetryCorpusStore.JsonOptions` is `internal` to `Guardrails.Core` and `Guardrails.Core.Tests` is in
its `InternalsVisibleTo` set, so you can use it directly. **Use it — never a fresh
`JsonSerializerOptions`.** A second spelling of the wire format is a second thing to drift.

Encode **exactly these six behaviours**, each as a `[Fact]` with **exactly the method name given**. The
names are pinned because this task's guardrail binds each behaviour to its method name in the runner's
TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | a row with all thirteen columns SET survives serialize → deserialize through `TelemetryCorpusStore.JsonOptions` with every value intact | `EveryPhase1ColumnRoundTripsThroughTheCorpusWireOptions` |
| 2 | a row with the thirteen UNSET still emits all thirteen camelCase keys, each with a JSON `null` — present, not omitted | `AnUnsetPhase1ColumnIsWrittenAsNull_NotOmitted` |
| 3 | a hand-written v1 corpus line — `schemaVersion: 1`, only the original fifteen keys, no Phase-1 key at all — still deserialises, and every Phase-1 column reads null | `AV1CorpusLineStillDeserializes_WithThePhase1ColumnsNull` |
| 4 | `TelemetryRow.CurrentSchemaVersion` is greater than 1 | `TheSchemaVersionIsBumpedPastOne` |
| 5 | by **reflection**: none of the thirteen properties is a C# `required` member | `NoPhase1ColumnIsRequired_SoAHistoricalRowStillReads` |
| 6 | by **reflection**: every value-typed Phase-1 column is `Nullable<T>` — `Turns`, `ActionMs`, `GuardrailMs`, `RouteWarm`, `CpuCount`, `TotalMemoryBytes`, `MaxParallelism` | `EveryValueTypedPhase1ColumnIsNullable_SoNoUnreportedFactReadsAsZero` |

### Why those six, and not thirteen property-exists tests

**Behaviours 2 and 3 are the pair that carries this collapsed task's weight.** A property-exists test
passes the moment the property is declared and tells you nothing about the wire. Behaviour 2 pins the
stable key set that makes the schema bump legible; behaviour 3 pins the append-only guarantee — that a
line already on an operator's disk still reads. Write behaviour 3's fixture as a **literal JSON string**
in the test, not by serializing a `TelemetryRow` and deleting keys: the point is a line this build never
produced.

**Behaviours 5 and 6 are the ones a hollow body cannot fake**, and each encodes a specific defect:
a `required` column makes every historical row throw, and a non-nullable value column makes an
unreported fact read as `0`. Both are invisible to behaviour 1, which passes perfectly against a row
whose `Turns` is `int` — it round-trips `0` just fine. Assert them off `typeof(TelemetryRow)`'s
properties by name, so the failure names the offending column.

**Behaviour 4 is deliberately narrow.** It asserts the constant SYMBOLICALLY and only that it moved past
1 — never against the literal `2`, so it survives the next bump. `TelemetryCorpusStoreTests`
`Append_EveryRowCarriesSchemaVersion` (line 130) is the precedent for reading it symbolically.

**Two shipped tests were checked against this bump and both survive**, so do not go looking for them to
fix: `TelemetryCorpusStoreTests.Append_EveryRowCarriesSchemaVersion` asserts
`TelemetryRow.CurrentSchemaVersion` symbolically rather than the literal 1, and
`TelemetryCorpusConcurrentAppendTests` line 61 hard-codes `SchemaVersion = 1` only to CONSTRUCT a row
and asserts nothing about the constant. If either is red after your change, something else is wrong —
report it rather than editing them; they are outside your writeScope.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Telemetry/TelemetryRow.cs` and
`tests/Guardrails.Core.Tests/Telemetry/CorpusRowShapeTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside those two paths — including
`TelemetryIngest.cs`, `TelemetryCorpusStore.cs`, neighbouring test files, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry.
