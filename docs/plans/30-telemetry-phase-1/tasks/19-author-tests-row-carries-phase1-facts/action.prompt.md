## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `19-author-tests-row-carries-phase1-facts`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "19-author-tests-row-carries-phase1-facts": { "someKey": "someValue" } }`.
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

This task authors the failing half of the corpus-row ETL work in
`docs/plans/30-telemetry-phase-1.md`. **Read sections 3.2, 3.3 and 3.4 in full** — 3.2 settles the
task-fingerprint bucket, 3.3 settles the model digest (IN Phase 1 in full, capture included), and 3.4
carries turns, segmented durations, warm/cold and the machine profile. Where this prompt and the plan
disagree, the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Do not touch provenance-on-failed-attempts.

## Both shapes already exist. Your tests COMPILE and fail at RUNTIME.

Two shape tasks ran before this one and both have landed:

- `03-extend-the-journal-record-shape` added the journal members you read FROM.
- `04a-extend-the-corpus-row-shape` added the thirteen corpus columns you assert ON, and bumped
  `TelemetryRow.CurrentSchemaVersion` to 2.

So every symbol you need is already there, and **the tests must compile.** They go red because
`src/Guardrails.Core/Telemetry/TelemetryIngest.cs` maps **none** of the thirteen columns yet — both of
its `new TelemetryRow { … }` sites stop where they stopped in Phase 0. That mapping is
`20-carry-phase1-facts-into-the-corpus-row`, the task that depends on this one.

**Failing is intentional; not compiling is a mistake to fix.** If you hit a compile error naming a
member that should exist, do not work around it and do not add it — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and
stop, because a shape task did not do its job and no test here can be trusted until it has.

## Task

Author **one** file, and only this one:
`tests/Guardrails.Core.Tests/Telemetry/Phase1TelemetryRowTests.cs`.

Class **`Phase1TelemetryRowTests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]` on
the class — the convention every shipped telemetry suite in this project uses (see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryIngestTests.cs:31`). Declare
`namespace Guardrails.Core.Tests.Telemetry;`, the namespace the shipped telemetry suites in this folder
already use.

### The idiom to follow

`tests/Guardrails.Core.Tests/Telemetry/TelemetryIngestTests.cs` is the file to read first and copy: it
builds a real `JournalDocument`, hands it to `TelemetryIngest.Ingest(journal, store, repo)` with a real
`TelemetryCorpusStore` over a per-test temp directory, reads the rows back off disk, and asserts on
them. **Do the same.** Do not construct a `TelemetryRow` in the test and assert on the object you just
built — that is a tautology that passes whatever the ETL does, and it is the single most likely way this
file goes green for the wrong reason.

Every test must therefore:

1. build a `JournalDocument` carrying the Phase-1 journal members task 03 added,
2. run it through `TelemetryIngest.Ingest` into a `TelemetryCorpusStore` pointed at the test's own temp
   directory (never `~/.guardrails/telemetry/` — a test that wrote there would poison the very data this
   plan exists to collect),
3. read the rows back and assert on the corpus row's column.

### The journal members you read FROM (task 03)

| journal member | grain |
|---|---|
| `TaskJournalEntry.Bucket : string?` | task — constant across a task's own retries within one run |
| `AttemptProvenance.ModelDigest : string?` | attempt |
| `AttemptProvenance.RouteWarm : bool?` | attempt |
| `AttemptRecord.Turns : int?` | attempt |
| `AttemptRecord.Segments : AttemptSegments?` (`ActionMs`, `GuardrailMs`) | attempt |
| `JournalDocument.Environment : RunEnvironment?` (`Host`, `Os`, `CpuCount`, `TotalMemoryBytes`, `MaxParallelism`, `HarnessVersion`, `SkillVersion`) | run |

Read `src/Guardrails.Core/Journal/JournalModel.cs` for their exact declarations rather than trusting
this table's spelling.

### The row columns you assert ON (task 04a)

`Bucket : string?`, `ModelDigest : string?`, `Turns : int?`, `ActionMs : long?`, `GuardrailMs : long?`,
`RouteWarm : bool?`, `Host : string?`, `Os : string?`, `CpuCount : int?`, `TotalMemoryBytes : long?`,
`MaxParallelism : int?`, `HarnessVersion : string?`, `SkillVersion : string?`.

`AttemptSegments` is FLATTENED onto the row as `ActionMs`/`GuardrailMs` — the row has no nested record.

### The two grains

`TelemetryIngest` writes two row shapes through one record, distinguished by `TelemetryRow.Attempt`
(see its class doc, and `TelemetryIngest.cs:61` and `:79`):

- **`Attempt == 0`** — the reserved sentinel, one task row per task per run. It carries identity, the
  declared tier and the terminal outcome. **It is a real row a reader strata on, not a placeholder.**
- **`Attempt >= 1`** — one row per attempt, retries included.

**The bucket and the run environment belong on BOTH grains. The attempt-scoped facts —
`ModelDigest`, `Turns`, `ActionMs`, `GuardrailMs`, `RouteWarm` — belong on the attempt row only,**
exactly as `Model` / `CostUsd` already are.

### Pinned behaviours

Encode **exactly these eight behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | an attempt row carries the task's bucket from `TaskJournalEntry.Bucket` | `TheAttemptRowCarriesTheBucket` |
| 2 | the `Attempt == 0` task-grain sentinel row carries the bucket too — a reader strata on it, and a task row without a bucket is a hole in exactly the column §3.2 exists to fill | `TheTaskGrainRowCarriesTheBucketToo` |
| 3 | an attempt row carries `AttemptProvenance.ModelDigest` | `TheAttemptRowCarriesTheModelDigest` |
| 4 | an attempt row carries `AttemptRecord.Turns` and both halves of `AttemptRecord.Segments`, flattened to `ActionMs` and `GuardrailMs` | `TheAttemptRowCarriesTurnsAndSegments` |
| 5 | an attempt row carries `AttemptProvenance.RouteWarm` — assert BOTH `true` and `false` reach the row, so an implementation that writes only the truthy case is caught | `TheAttemptRowCarriesRouteWarmth` |
| 6 | `JournalDocument.Environment`'s seven members reach EVERY row of BOTH grains — the machine profile is a run fact, and a stratified comparison that cannot see it pools a 64GB box with a 128GB one (§3.4) | `EveryRowCarriesTheRunEnvironment` |
| 7 | the ETL STAMPS `TelemetryRow.CurrentSchemaVersion` onto every row it writes, and that constant is greater than 1 | `TheSchemaVersionSaysTheRowShapeChanged` |
| 8 | a journal that reports none of the Phase-1 attempt facts leaves `Turns`, `ActionMs`, `GuardrailMs`, `ModelDigest` and `RouteWarm` **null, never 0 and never false** | `AnUnreportedPhase1Fact_StaysNull_NotZero` |

### Two of these eight are GREEN when you finish, and that is correct

**Behaviour 7** — task 04a already bumped the constant, and the ETL has stamped
`TelemetryRow.CurrentSchemaVersion` at both construction sites since Phase 0. So a correct test passes
today. It is **not** a duplicate of 04a's `TheSchemaVersionIsBumpedPastOne`: that one reads the constant,
this one asserts the ETL puts it on the rows it emits — the constant could be bumped and the ETL could
still stamp a literal. Assert it SYMBOLICALLY (`TelemetryRow.CurrentSchemaVersion`), never against the
literal `2`; `TelemetryCorpusStoreTests.Append_EveryRowCarriesSchemaVersion` (line 130) is the precedent
and a symbolic assertion survives the next bump.

**Behaviour 8** — the columns exist and nothing populates them, so they are already null. A correct test
passes today, and after task 20 it is the check that stops the mapping coalescing an unreported fact into
a value.

**Both are DECLARED EXEMPTIONS in this task's guardrail** (`Expect = 'Executed'`): it asserts they RAN,
not that they were red. **Do not "fix" either into failing, and do not mark either `[Fact(Skip=…)]`** —
a skipped exemption is no coverage at all. The other six call an ETL that maps nothing, so a correct
test is red for all six.

### Notes on three of the six red ones

**Behaviour 8 is §15.2's null-versus-zero rule** and the reason it earns a test of its own even while
green: `TelemetryRow.CostUsd`'s doc comment (lines 57-63) already draws it — *null* means the runner
never reported a cost, which is **not the same claim** as a recorded `0`. A script action runs no turns
and a runner that reports no usage reports no duration; writing `0` there would make the corpus assert
the attempt took no time, a measurement nobody made. `RouteWarm` carries it one step further: `null` is
*"no route resolved"*, which is not `false` (*"the route was cold"*).

**Behaviour 5 needs both polarities in one test.** `RouteWarm` is a `bool?`. A test that only checks
`true` is satisfied by an implementation that hardcodes `true`; a test that only checks `false` is
satisfied by one that never assigns at all. Drive two attempts in the same journal, one of each, and
assert the nullable value **equals** `false` and `true` respectively — `Assert.Equal(false, row.RouteWarm)`
rather than `Assert.False(row.RouteWarm)`, so the failure message distinguishes null from false instead
of collapsing them.

**Behaviour 6 must check every row, not the first one.** Seven members reaching the attempt row while
the task row gets six is exactly the kind of half-mapping a spot check misses.

## Out of scope, stated so you do not drift into it

- **Do not implement anything.** No `src/**` edit of any kind. `TelemetryIngest.cs` is task 20's, and
  `TelemetryRow.cs` is task 04a's and already done.
- **Do not write the report-side tests.** The bucket column, the digest's role in the model fingerprint
  and the era boundary are `21-author-tests-report-and-era-boundary`'s, in the Integration project.
- **Do not touch the shipped telemetry suites.** `TelemetryIngestTests`, `TelemetryCorpusStoreTests`,
  `TelemetryReportTests` and `TelemetryFailureClassifierTests` pin the Phase-0 behaviour every Phase-1
  field extends, and the plan's preflight reads its baseline off them.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Telemetry/Phase1TelemetryRowTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path — including
`src/Guardrails.Core/Telemetry/TelemetryIngest.cs` (the tempting "fix" for the six red tests), other
production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry.
