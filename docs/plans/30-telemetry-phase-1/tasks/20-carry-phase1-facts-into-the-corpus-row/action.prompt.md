## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `20-carry-phase1-facts-into-the-corpus-row`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "20-carry-phase1-facts-into-the-corpus-row": { "someKey": "someValue" } }`.
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

This task implements the corpus-row ETL mapping in `docs/plans/30-telemetry-phase-1.md`. **Read sections
3.2, 3.3 and 3.4 in full** — they say what each column is FOR, which is what decides which grain it
rides. Where this prompt and the plan disagree, the plan is authoritative and you should say so in your
summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Do not touch provenance-on-failed-attempts.

## What is already done, so you do not redo it

- **`04a-extend-the-corpus-row-shape` declared all thirteen columns on `TelemetryRow` and bumped
  `CurrentSchemaVersion` to 2.** `src/Guardrails.Core/Telemetry/TelemetryRow.cs` is **outside your
  writeScope** — do not add a column, do not change a type, do not touch the constant. If a column you
  need is genuinely missing, that is a shape-task failure: write
  `{"needsHuman": {"question": "<which column>", "kind": "blocked-work"}}` and stop.
- **`03-extend-the-journal-record-shape` landed every journal member you read FROM.**
- **`19-author-tests-row-carries-phase1-facts` authored the eight failing tests.** Six are red because
  nothing maps the columns; two (`TheSchemaVersionSaysTheRowShapeChanged` and
  `AnUnreportedPhase1Fact_StaysNull_NotZero`) are green already and **must stay green** — the second one
  is precisely the check that catches you coalescing an unreported fact into a value.

**Do NOT edit `tests/Guardrails.Core.Tests/Telemetry/Phase1TelemetryRowTests.cs`.** It is outside your
writeScope; an edit there fails the write-scope check and burns a retry. If a test is genuinely wrong or
incompatible with the plan, escalate as `blocked-work` rather than changing it.

## Task

One file, and only this one: `src/Guardrails.Core/Telemetry/TelemetryIngest.cs`.

There are exactly two `new TelemetryRow { … }` sites in it and **both must be edited**:

- **line 61** — the task-grain sentinel (`Attempt = 0`), one per task per run.
- **line 79** — the per-attempt row, one per attempt, retries included.

| column | task-grain row (`:61`) | attempt row (`:79`) | source |
|---|---|---|---|
| `Bucket` | **yes** | **yes** | `task.Bucket` (`TaskJournalEntry`) — a TASK fact, constant across a task's own retries within one run |
| `Host`, `Os`, `CpuCount`, `TotalMemoryBytes`, `MaxParallelism`, `HarnessVersion`, `SkillVersion` | **yes** | **yes** | `journal.Environment` (`JournalDocument`) — a RUN fact |
| `ModelDigest` | no | **yes** | `provenance?.ModelDigest` |
| `RouteWarm` | no | **yes** | `provenance?.RouteWarm` |
| `Turns` | no | **yes** | `attempt.Turns` |
| `ActionMs`, `GuardrailMs` | no | **yes** | `attempt.Segments?.ActionMs` / `?.GuardrailMs` |

**Both sites, and the split between them, is the whole content of this task.** Editing only the attempt
row is the single most likely wrong implementation: it makes six of the eight tests pass and leaves
`TheTaskGrainRowCarriesTheBucketToo` and half of `EveryRowCarriesTheRunEnvironment` red, which reads like
two odd test failures rather than a missed grain.

The attempt-scoped facts stay off the task row for exactly the reason `Model` and `CostUsd` already do:
a task row summarizing several attempts cannot carry one attempt's route or one attempt's turn count
without inventing a number nobody measured. The bucket and the environment are different — they are the
same value for every row of the task and of the run respectively, so putting them on both grains costs
nothing and lets a reader strata on the task row alone.

**Do not synthesize a value.** Every one of these is `null` when the journal does not carry it. No
`?? 0`, no `?? false`, no `?? "(unknown)"`, no `?? string.Empty`. That is §15.2's null-versus-zero rule
and it has a test of its own (`AnUnreportedPhase1Fact_StaysNull_NotZero`) that is GREEN right now — the
only way to redden it is to add exactly such a coalesce.

`AttemptSegments` is flattened: `attempt.Segments?.ActionMs` and `attempt.Segments?.GuardrailMs` become
two plain columns. The `?.` matters — a null `Segments` must leave both columns null, not throw.

Update the class doc if the mapping's shape makes an existing sentence stale — but **do not delete the
paragraph explaining the two grains and the reserved `Attempt = 0` sentinel**; it is the contract the
report reads the corpus back through.

## Out of scope, stated so you do not drift into it

- **The row declaration is not yours** (task 04a, above). Neither is the schema-version constant.
- **The report is not yours.** `src/Guardrails.Cli/Commands/TelemetryCommand.cs` still renders
  `(unbucketed)` unconditionally after this task, and that is correct: sourcing the bucket column,
  folding the digest into the model fingerprint and printing the era boundary are
  `22-render-the-bucket-digest-and-era-boundary`'s.
- **Nothing populates these journal members end-to-end yet either.** Tasks 06, 10, 12, 12a, 14, 16 and 18
  do that. Your job is the ETL mapping, which is testable on a hand-built `JournalDocument` today.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Telemetry/TelemetryIngest.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including `TelemetryRow.cs`, the authored test
file, other production files, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry.
