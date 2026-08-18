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

### The stub — deliberately NAIVE, and that is the point

In `src/Guardrails.Core/Journal/JournalModel.cs`:

1. Declare a **`public sealed record AttemptJudge`** with `Runner`, `Kind`, `Model`, `Effort`, `Tier`,
   `Strength`, **`Bumped`** and **`Advisory`** (DoR §12.4, eight members).
2. Hang it off **`AttemptProvenance`** as an optional `AttemptJudge? Judge { get; init; }` member.

**Placement is SETTLED — `AttemptProvenance`, per DoR §12.4's D32.** Do not re-derive it, and do not
move it to `AttemptRecord`. The reason is mechanical: `AttemptProvenance` is the ONLY member that
already rides `PendingAttempt` (`AttemptJournaler.cs:212`), so it reaches BOTH attempt-record
construction paths — the serial journaller AND `Scheduler.RecordSucceededSettle`, which is the
DEFAULT worktree mode. A member hung directly off `AttemptRecord` reaches the serial path and
silently vanishes in worktree mode. "The facts the harness knows at attempt launch" describes when
`AttemptProvenance` is CONSTRUCTED, not what may be recorded on it before the record is written; task
08 folds the judge in with a `with` expression at settle time, and the value reaches both paths for
free.

**Now the naive part.** Declare all of it **WITHOUT** the
`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` attribute on the new `Judge` member —
a plain `public AttemptJudge? Judge { get; init; }`. That is what makes this task's red REAL: a stub
carrying the attribute already satisfies every behaviour below, leaving task 04 nothing to implement
and this task no way to fail. Adding the attribute is task 04's entire deliverable. Do not add it
here, and do not "helpfully" copy the attribute off the neighbouring members while following their
shape in every other respect.

You should expect behaviours **2** and **5** to FAIL on your stub, and 1/3/4 to pass. That is the
correct outcome — `02-tests-fail-on-current-code` requires at least one genuine failure.

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
5. **Nothing else in the provenance changed shape** — assert an attempt provenance carrying NO judge
   still serializes exactly as it did before: no `judge` key at all. This is the regression every
   schema task risks, and on the naive stub it is a REAL one (a null-emitting member changes the
   output of every existing provenance).
6. **`Advisory` rides the judge object** — a judge resolved with a §6.5 weak-verifier finding records
   that text; a judge with no finding records it absent. It is optional and independent of `Bumped`
   (a judge can be advisory-flagged without a bump having fired).

### The test METHOD NAMES are PINNED

Your `03-covers-key-behaviors` guardrail matches DISCOVERED test names, never file text. Add more
tests freely; do not rename these.

| behaviour | method name |
|---|---|
| round-trip | `Judge_RoundTrips_WhenPresent` |
| absent-not-null | `Judge_AbsentFromJson_WhenNull` |
| backward compat | `OlderJournal_WithoutJudge_StillReads` |
| the bump datum | `Bumped_RecordsFalse_NotAbsent_WhenNoBumpFired` |
| nothing else moved | `ProvenanceWithoutJudge_SerializesUnchanged` |
| the advisory datum | `Advisory_RoundTrips_AndIsAbsentWhenNoFinding` |

Tests must **COMPILE and FAIL** — failing is intentional; NOT compiling is a mistake to fix. Do not
implement the serialization behaviour; task 04 does.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/JudgeProvenanceSchemaTests.cs` and
`src/Guardrails.Core/Journal/JournalModel.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside those paths — including `JournalJson.cs` (task 04),
`AttemptJournaler.cs` (task 08), or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT
edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
