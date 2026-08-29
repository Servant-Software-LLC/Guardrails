## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `05-author-tests-journal-etl`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "05-author-tests-journal-etl": { "someKey": "someValue" } }`.
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

## Task

Author the FAILING tests, plus the minimal stub they compile against, for the **journal-to-corpus ETL**
— the pass that turns a plan's `state/run.json` into corpus rows.

**Write only to these two files:**
- `tests/Guardrails.Core.Tests/Telemetry/TelemetryIngestTests.cs`
- `src/Guardrails.Core/Telemetry/TelemetryIngest.cs` (stub)

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Telemetry/TelemetryIngestTests.cs` and
`src/Guardrails.Core/Telemetry/TelemetryIngest.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including changes to other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The test class MUST be named `TelemetryIngestTests`** in namespace `Guardrails.Core.Tests.Telemetry`,
with `[Trait("Category", "ModelEvidence")]` on the class and every method. The guardrails filter on
exactly `Category=ModelEvidence&FullyQualifiedName~TelemetryIngestTests`.

**Pin these six behaviours to these exact test method names:**

| behaviour | test method name |
|---|---|
| one task row per task per run | `Ingest_EmitsOneTaskRowPerTaskPerRun` |
| one attempt row per attempt, retries included | `Ingest_EmitsOneAttemptRowPerAttempt_RetriesIncluded` |
| route provenance carried onto the attempt row | `Ingest_CarriesRouteProvenanceOntoTheAttemptRow` |
| unreported cost and tokens stay null, not zero | `Ingest_UnreportedCostAndTokens_StayNull_NotZero` |
| re-ingesting the same run adds no rows | `Ingest_SameRunTwice_AddsNoDuplicateRows` |
| guardrail-failed rows carry the classified kind | `Ingest_GuardrailFailedRows_CarryTheClassifiedFailureKind` |

**Design constraints the tests must encode** (charter §3.1, "two grains, both recorded"):
- The ETL takes a `JournalDocument` (read `src/Guardrails.Core/Journal/JournalModel.cs` for its real
  shape — build fixtures from the real record types, never from hand-written JSON strings) plus the
  corpus store and the failure classifier, and writes rows.
- **Two grains.** A **task row** per task per run carrying `definitionHash`, plan/task/run ids, declared
  tier and its origin, and the terminal outcome. An **attempt row** per attempt carrying the route
  (`model`, `requestedModel`, `runner`, `kind`, `tier`, `tierSource`, `effort`), timings, outcome, cost
  and usage.
- **Every attempt counts, retries included.** Folding a task down to its final attempt under-reports by
  exactly the retry spend — which is the spend the measurement most needs to see. Assert two attempts
  produce two rows.
- **Unreported cost or usage stays null.** Never `0`. `JournalTierSpend` draws this distinction already;
  the corpus keeps it.
- **Re-ingest is a no-op**, on the store's `(runId, taskId, attempt)` idempotency. This is what makes
  backfilling a directory of plans safe to re-run.
- **A `guardrail-failed` attempt carries the classifier's verdict**, so the corpus can tell a
  write-scope violation from a failed test — including the `undifferentiated` case.

**The tests MUST COMPILE and FAIL** against a `NotImplementedException` stub. Do NOT implement the ETL.
