## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/02-implement-journal-tiering-schema`, NOT the
  stableId and NOT the bare folder name. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/02-implement-journal-tiering-schema": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make `tests/Guardrails.Core.Tests/ModelTiering/JournalTieringSchemaTests.cs` — authored by
`01-author-tests-journal-tiering-schema` — pass. **Do NOT edit those tests.** If they are genuinely
wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than changing
them; an out-of-scope edit to a test file fails the write-scope check and burns a retry.

**`docs/plans/17-model-tiering.md` §12.4 is the design of record and wins over any paraphrase here.**

The model declarations already exist (task 01 wrote them). What is missing is the **wire mapping**,
which lives in `src/Guardrails.Core/Journal/JournalJson.cs` alongside every other one:

1. **`OutcomeToken`** gains an `AttemptOutcome.NoRoute => "no-route"` arm, and the
   `AttemptOutcomeConverter.Read` switch gains the matching `"no-route" => AttemptOutcome.NoRoute`.
   Both directions, or a written journal cannot be re-read — and the harness re-reads its own journal
   on every resume. Leave both `_ =>` throw arms in place: an unrecognised token must stay reported,
   never silently defaulted.
2. **`TierSource`** gains the same treatment: a `TierSourceToken(TierSource)` helper mirroring
   `OutcomeToken` (the single source of truth for the kebab spelling), a `JsonConverter<TierSource>`
   registered in the same options as its siblings, and the wire tokens
   **`"task"` / `"plan-default"` / `"override"`**. `plan-default` is why this is an explicit mapping
   and not `Enum.ToString` — the same reason `PromptRunnerKinds.Token` is explicit for
   `openai-compat`.
   The property is `TierSource?`; make sure a **null** stays absent, not `"null"`.

Keep every addition **additive**: an existing `run.json` written before this change must read without
error, and one written after it must contain **none** of the new keys when nothing set them
(`JsonIgnoreCondition.WhenWritingNull`, exactly as the existing provenance members do). That
absent-not-null property is Invariant 7's journal half — a single-model user's `run.json` must be
byte-identical to what it is today.

If task 01's declarations need a small correction to make the mapping work (an enum member in the
wrong position, a missing `[JsonIgnore]`), fix it in `AttemptOutcome.cs` / `JournalModel.cs` — both
are in your scope. Do not move a declaration into a new file.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Journal/JournalJson.cs`,
`src/Guardrails.Core/Journal/JournalModel.cs` and `src/Guardrails.Core/Journal/AttemptOutcome.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these
paths — including the test file, other production files, or the `.csproj`. An out-of-scope edit fails
the task immediately and consumes a retry.

Nothing WRITES the new provenance members yet — the attempt-launch wiring (task 07) is their first
producer, and the run-report aggregation (task 11) their first reader. This task lands the wire
contract only; do not wire it into the executor.
