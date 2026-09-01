## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `23-author-tests-attribution-census`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "23-author-tests-attribution-census": { "someKey": "someValue" } }`.
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

This task implements the first half of section **3.3a** of `docs/plans/30-telemetry-phase-1.md`.
**Read section 3.3a in full**, and read section 2 as well — section 2 is the finding that explains why
an unattributed row matters at all. Where this prompt and the plan disagree, the plan is authoritative
and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Do not touch provenance-on-failed-attempts. That fix matters here for one reason only: it is
**forward-only**. Forty-eight failed rows now carry provenance where zero did before, but 92 older
failed rows still do not — so the recording gap this census measures is mostly, but *not provably
entirely*, history. **The census measures it; it does not assume it is closed.**

## SCOPE — read this before you write anything

**Section 3.3a decided that Phase 1 owns the CENSUS ONLY, and that the FIX ships as its own issue
(#577).** This is a maintainer decision recorded in the plan, not a suggestion:

> Phase 1's deliverable is the split, not the repair: **what fraction of the 313 `None` rows are script
> actions** — correct by construction, since a script invokes no model — **versus a genuine recording
> gap.** Until that number exists, "close it" has no defined scope, and committing a phase to closing an
> unscoped defect is how a phase slips.

So: **do NOT change how attribution is recorded.** Do not add a provenance write, do not widen a
journal member, do not "helpfully" populate a model where one is missing, and do not close #577.
Measuring the gap and repairing it are two different deliverables, and this plan owns only the first.
If while writing these tests you find the exact site where attribution is being dropped, that is a
**finding to report in your summary**, not work to do — write it down and leave the code alone.

## Task

Author **two** files, and only these two.

### 1. `src/Guardrails.Core/Telemetry/TelemetryAttributionCensus.cs` — the minimal stub

A `public static class TelemetryAttributionCensus` in namespace `Guardrails.Core.Telemetry`, carrying
one method with **exactly this signature**:

```csharp
public static AttributionCensusResult Census(string planFolderOrDirectory)
```

whose body is `throw new NotImplementedException();`.

Plus the result records, in the same file, with **exactly these member names** — task 24 implements
against them and its own guardrail binds to the tests you write here, so a renamed member reads as an
absent behaviour:

```csharp
public sealed record AttributionCensusResult
{
    public required int TotalRowsNamingNoModel { get; init; }
    public required int TaskGrainRows { get; init; }
    public required int ScriptActionRows { get; init; }
    public required int RecordingGapRows { get; init; }
    public IReadOnlyList<AttributionCensusPlan> Plans { get; init; } = [];
    public IReadOnlyList<string> SkippedFolders { get; init; } = [];
    public IReadOnlyList<string> UnreadableDefinitions { get; init; } = [];
}

public sealed record AttributionCensusPlan
{
    public required string PlanFolder { get; init; }
    public required int TotalRowsNamingNoModel { get; init; }
    public required int TaskGrainRows { get; init; }
    public required int ScriptActionRows { get; init; }
    public required int RecordingGapRows { get; init; }
}
```

Four things about that shape are load-bearing, and each one is a decision this breakdown made that the
plan left open — a maintainer may replace any of them, but the tests must pin whichever one stands:

1. **`TaskGrainRows + ScriptActionRows + RecordingGapRows == TotalRowsNamingNoModel`, always.** The
   three categories are exhaustive over what the census could classify, which is what makes the
   headline fraction (`RecordingGapRows / TotalRowsNamingNoModel`) a real number rather than a
   proportion of an unstated denominator.
2. **`UnreadableDefinitions` exists so that identity can stay total.** An attempt whose `task.json`
   cannot be read or parsed cannot be told apart as script-versus-prompt, so it is named here and
   counted in NONE of the four counts. Booking it as a recording gap would inflate the defect with
   things nobody measured; silently dropping it would shrink the denominator with no trace. This is
   the same rule §15.4 of `docs/plans/02-schemas-and-contracts.md` already states for an unrecognised
   guardrail failure: it is recorded `undifferentiated` and **never guessed at**.
3. **`SkippedFolders` is the reported no-op.** A plan folder with no `state/run.json` is not an error —
   `TelemetryIngest.IngestPlanFolder` already sets that precedent, and backfill is pointed at
   directories of plans, some of which never ran.
4. **`PlanFolder` is a folder NAME, never an absolute path.** §15.1 of the SSOT: the corpus "records
   facts and identifiers only: no prompt text, no file contents, no diffs, no absolute paths," and
   `TelemetryRow.repo` is the workspace directory NAME for exactly this reason. The census output is
   the same kind of artifact and takes the same rule.

**Do NOT give `Census` a `--corpus-root`-shaped parameter, a `TelemetryCorpusStore`, or any other
corpus dependency.** See "why the plan folders" below: the census reads plan folders, and a census
that also touched the corpus would be able to write to the operator's real one.

### 2. `tests/Guardrails.Core.Tests/Telemetry/AttributionCensusTests.cs` — the failing tests

Class **`AttributionCensusTests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]` on
the class (the convention every shipped telemetry suite in this project uses — see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs:15`).

Encode **exactly these seven behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | a task-grain sentinel row (`Attempt = 0`) names no model **by construction** and is counted as correct, never as a gap | `ATaskGrainSentinelRow_CountsAsCorrectByConstruction` |
| 2 | an attempt of a task whose action is a SCRIPT names no model **by construction** — a script invokes no model — and is counted as correct | `AScriptActionAttempt_CountsAsCorrectByConstruction` |
| 3 | an attempt of a task whose action is a PROMPT, journalled with no provenance (or with provenance naming no model), is the **recording gap** — the one category that is a defect | `APromptAttemptWithNoProvenance_CountsAsARecordingGap` |
| 4 | a prompt attempt that DOES name a model is outside the census entirely — it counts in no category and does not move the total | `APromptAttemptWithProvenance_CountsInNoCategory` |
| 5 | the three categories sum EXACTLY to `TotalRowsNamingNoModel` — assert the arithmetic, over a fixture containing all three | `TheThreeCategoriesSumToTheTotalNamingNoModel` |
| 6 | one unparseable `task.json` is named in `UnreadableDefinitions` and the scan continues — the rest of that plan folder is still censused, and nothing throws | `AMalformedTaskJson_IsSkipped_NotFatal` |
| 7 | a plan folder with no `state/run.json` appears in `SkippedFolders`, contributes zero rows, and is not an error | `APlanFolderWithNoJournal_IsAReportedNoOp` |

**The three-way split is the finding, and the third category is the only defect.** Behaviours 1 and 2
exist to stop the census reporting a number that is 95% correctness. A task-grain sentinel row never
carries a model by construction — `TelemetryIngest.cs` builds it (grep for `Attempt = 0`) setting only
tier and tier-source, deliberately — and a script action invokes no model at all. Reporting either as
an attribution gap would hand #577 a scope that is mostly not a defect, which is precisely the
"unscoped defect" section 3.3a says a phase slips on.

### Why the census answers from the PLAN FOLDERS, not from the corpus rows

State this in your test file's class doc, because it is the design decision most likely to be
"simplified" later: **a corpus row cannot be joined back to the task definition that would answer the
question.** `TelemetryRow` carries `runId`, `taskId` and `repo` — and `repo` is a directory NAME, not a
path (§15.1) — so there is no way from a row to the `task.json` that says whether the action was a
script. Reading `state/run.json` beside `tasks/<id>/task.json` answers it **at the source**, where both
facts are present together.

Walk a directory of plan folders **the way `TelemetryCommand`'s `ingest` verb already does**: a folder
that is itself a plan is censused; otherwise its immediate children are, one level deep and no further
(recursing would start censusing a plan's own subdirectories on the strength of a coincidental path
shape). Be fault-tolerant the same way `TryIngestPlanFolder` is — it catches exactly
`IOException | UnauthorizedAccessException | JsonException` and reports the failure against ITS folder
rather than aborting the scan. **Do not catch bare `Exception`**: the point of that narrow filter is
that a bug in the census still throws.

### How every test must be written

Every test must **invoke `TelemetryAttributionCensus.Census` and assert on its return value.** A test
that builds a fixture and asserts something about the fixture without calling `Census` is hollow: it
passes against the `NotImplementedException` stub and this task's guardrail will name it.

Build each fixture as a real plan folder on disk in a temp directory — `state/run.json` written through
the journal's own serializer (`JournalJson.Options`), plus `tasks/<id>/task.json` and a real action file
beside it so the action kind is genuine rather than asserted. Do NOT introduce a test double, an
interface or an in-memory filesystem abstraction: the whole subject of this census is what is actually
on disk, and a fake would let the implementation pass while the real directory walk is broken.

**Do NOT implement `Census`.** The tests MUST COMPILE and FAIL against the throwing stub — failing is
intentional; not compiling is a mistake to fix. All seven fail: the stub throws unconditionally, so
there are no exemptions in this pair.

**Do NOT write the CLI verb or an Integration test here.** `telemetry census` and
`tests/Guardrails.Integration.Tests/Commands/TelemetryCensusCommandTests.cs` belong to task 24 and are
outside your `writeScope`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Telemetry/AttributionCensusTests.cs` and
`src/Guardrails.Core/Telemetry/TelemetryAttributionCensus.cs` (the stub file). After this task
completes, the harness runs a `git diff` check and rejects any edit outside these paths — including
changes to other production files, neighbouring test files, `TelemetryIngest.cs`, `TelemetryRow.cs`, or
the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
