## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-author-tests-corpus-store`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-author-tests-corpus-store": { "someKey": "someValue" } }`.
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

Author the FAILING tests, plus the minimal stubs they compile against, for the telemetry corpus
store — the append-only local record described in `docs/plans/model-evidence-and-graduation.charter.md`
§9 (Storage, privacy, ingest).

**Write only to these three files:**
- `tests/Guardrails.Core.Tests/Telemetry/TelemetryCorpusStoreTests.cs` — the tests
- `src/Guardrails.Core/Telemetry/TelemetryRow.cs` — the row record (stub)
- `src/Guardrails.Core/Telemetry/TelemetryCorpusStore.cs` — the store (stub)

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Telemetry/TelemetryCorpusStoreTests.cs`,
`src/Guardrails.Core/Telemetry/TelemetryRow.cs` and
`src/Guardrails.Core/Telemetry/TelemetryCorpusStore.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including changes to other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The test class MUST be named `TelemetryCorpusStoreTests`** in namespace
`Guardrails.Core.Tests.Telemetry`, and **every test method carries
`[Trait("Category", "ModelEvidence")]`** (also put the trait on the class). The guardrails filter on
exactly `Category=ModelEvidence&FullyQualifiedName~TelemetryCorpusStoreTests`, so a different class name
makes a correct implementation unverifiable.

**Pin these six behaviours to these exact test method names** — the red census guardrail binds each
behaviour to its method name and will not accept a rename:

| behaviour | test method name |
|---|---|
| one JSON object per line, appended | `Append_WritesOneJsonLinePerRow` |
| idempotent on `(runId, taskId, attempt)` | `Append_SameRunTaskAttemptTwice_WritesOnlyOneRow` |
| month-rotated file name | `Append_WritesIntoAMonthRotatedFile` |
| `schemaVersion` on every row | `Append_EveryRowCarriesSchemaVersion` |
| opt-out writes nothing at all | `Append_WhenCollectionDisabled_WritesNothing` |
| purge removes every row | `Purge_RemovesEveryRowUnderTheCorpusRoot` |

**Design constraints the tests must encode** (from the charter, §9):
- The store takes its **corpus root directory as a constructor parameter**. It must NEVER resolve
  `~/.guardrails/telemetry/` itself in these tests — every test points it at a fresh temp directory it
  deletes afterwards. (Resolving the real default home path belongs to the CLI task, not here.)
- **Append-only JSONL**: one JSON object per line, never a rewritten array. A second append must leave
  the first line byte-identical.
- **Month rotation**: rows land in a file whose name carries the row's UTC year and month, so a corpus
  grows by file rather than without bound. Assert the actual file name.
- **`schemaVersion`** is present on every row. Assert it on a round-trip, not by string-matching the
  writer.
- **Idempotent on `(runId, taskId, attempt)`**: appending the same triple twice leaves exactly one row.
  This is what makes re-ingesting a plan safe by construction, so test it by appending twice and counting.
- **Opt-out**: when collection is disabled the store writes NOTHING — assert the corpus directory has no
  files at all, not merely that a row is absent.
- **Purge** removes every row under the corpus root and is safe to call on an empty corpus.

`TelemetryRow` is a record carrying at minimum: `schemaVersion`, `runId`, `taskId`, `attempt`,
`startedAt`, `endedAt`, `outcome`, `model`, `runner`, `kind`, `tier`, `tierSource`, `effort`, `costUsd`,
`inputTokens`, `outputTokens`, `repo`. Cost and token fields are **independently nullable** — null means
"never reported", which is NOT the claim zero makes (the charter's §6 rule, and the distinction
`JournalTierSpend` already draws).

**The tests MUST COMPILE and FAIL.** Write the stubs so the test project builds: members that throw
`NotImplementedException` (or return `default`). Failing is the point; NOT compiling is a mistake to fix.
Do NOT implement the behaviour — that is the next task's job.
