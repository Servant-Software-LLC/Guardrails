## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/03-author-tests-judge-provenance-schema`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/03-author-tests-judge-provenance-schema": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the **failing tests** for DoR §12.4's **judge provenance object**, plus the schema stub they
compile against.

- **`tests/Guardrails.Core.Tests/ModelTiering/JudgeProvenanceSchemaTests.cs`**
- class **`JudgeProvenanceSchemaTests`**, `[Trait("Category", "TierResolution")]` at class level.

### The stub

In `src/Guardrails.Core/Journal/JournalModel.cs`, declare a **`public sealed record AttemptJudge`**
with `Runner`, `Kind`, `Model`, `Effort`, `Tier`, `Strength` and **`Bumped`**, and hang it off the
attempt record as an optional member with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`.
**Read how `AttemptProvenance` and `AttemptUsage` already do this and follow it exactly** — the
absent-not-null discipline and the `JsonIgnore` attribute are the house pattern, not a choice.

**Where it hangs is a real decision, and task 07 has to live with it.** Wave 2 learned this the
expensive way (#474/#475): a member is useless if the datum cannot reach it. A judge is resolved
during **guardrail evaluation**, i.e. AFTER attempt launch — so it cannot ride launch-time
`AttemptProvenance`, which is documented as "the facts the harness already knows at attempt launch".
Note that `AttemptRecord.FailedGuardrails` IS post-guardrail data that already makes the trip
(`GuardrailRunner` → `TaskExecutor` → `AttemptJournaler`). Follow that sibling. If you conclude the
member belongs somewhere the datum demonstrably cannot reach, say so in your state-out fragment
under a `notes` key rather than declaring it anyway.

### Behaviours the tests MUST encode

1. **Round-trips when present** — every member survives a serialize/deserialize cycle, `Bumped`
   included.
2. **ABSENT when null, not `"judge": null`.** Assert on the emitted JSON TEXT that the key does not
   appear at all — a structural assertion on a deserialized object cannot tell absent from null, and
   absent is the contract (old journals must read clean and a script attempt adds no noise).
3. **An older journal with no judge key reads fine** and yields a null member — backward
   compatibility is the reason the field is optional.
4. **`Bumped` is meaningful on its own** — a judge that resolved without a bump records `false`, not
   absent. It is the datum #230-lite reads to answer "is a bumped judge worth what it costs".
5. **Nothing else in the record changed shape** — assert an existing attempt record still serializes
   as it did. This is the regression every schema task risks.

### The test METHOD NAMES are PINNED

Your `03-covers-key-behaviors` guardrail matches DISCOVERED test names, never file text. Add more
tests freely; do not rename these.

| behaviour | method name |
|---|---|
| round-trip | `Judge_RoundTrips_WhenPresent` |
| absent-not-null | `Judge_AbsentFromJson_WhenNull` |
| backward compat | `OlderJournal_WithoutJudge_StillReads` |
| the bump datum | `Bumped_RecordsFalse_NotAbsent_WhenNoBumpFired` |

Tests must **COMPILE and FAIL** — failing is intentional; NOT compiling is a mistake to fix. Do not
implement the serialization behaviour; task 04 does.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/JudgeProvenanceSchemaTests.cs` and
`src/Guardrails.Core/Journal/JournalModel.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside those paths — including `JournalJson.cs` (task 04),
`AttemptJournaler.cs` (task 07), or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT
edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
