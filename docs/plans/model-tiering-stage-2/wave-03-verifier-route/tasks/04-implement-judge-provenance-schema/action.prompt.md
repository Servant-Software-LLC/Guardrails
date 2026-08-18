## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/04-implement-judge-provenance-schema`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/04-implement-judge-provenance-schema": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make the judge provenance schema round-trip so the tests authored by
`03-author-tests-judge-provenance-schema` pass. **Do NOT edit those tests** — if they are genuinely
wrong, write `{"needsHuman": "<why>"}` to the state-out path instead.

**`docs/plans/17-model-tiering.md` §12.4 is the design of record.**

The work is serialization discipline, not new modelling:

- **Absent, never null.** `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on the
  member, matching how `AttemptProvenance` and `AttemptUsage` already do it. A `"judge": null` on
  every script attempt is new noise in `run.json` for users who never opted into any of this.
- **Old journals read unchanged.** The member is optional; a journal written before this wave
  deserializes with it null and nothing else shifts.
- **Do not renumber, rename or reorder anything already in the record.** Whatever the tests assert
  about existing shape is the regression bar.

Follow the conventions already in `JournalJson.cs` — options, naming policy, converters — rather
than introducing a parallel style for one record.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Journal/JournalModel.cs` and `src/Guardrails.Core/Journal/JournalJson.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside those
paths — including the test file, `AttemptJournaler.cs`, or the `.csproj`. An out-of-scope edit fails
the task immediately and consumes a retry.

**Populating the field is NOT your job** — task 08 carries the datum from `GuardrailRunner` to the
record. You are landing the schema it will write into. Do not touch the execution path.
