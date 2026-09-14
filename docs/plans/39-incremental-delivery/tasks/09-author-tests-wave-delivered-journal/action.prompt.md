## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `09-author-tests-wave-delivered-journal`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-author-tests-wave-delivered-journal": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author failing tests AND the stubs for the wave delivery record — design 39 §4 ("The record, pinned at
review") and §5 ("Wiring and halts"). Read §4 first: it is the contract these tests encode.

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveredJournalTests.cs`
**Test class:** `WaveDeliveredJournalTests`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**The record** lives on `run.json`'s `waves.<dir>.delivered`, camelCase on the wire through
`JournalJson.Options`:

| Member | Type | Meaning |
|---|---|---|
| `Status` | `required WaveDeliveryStatus` | `running` \| `delivered` \| `refused` \| `suppressed` |
| `StartedAt` | `required DateTimeOffset` | written with `running`, before anything the operator can see moves (#625) |
| `At` | `DateTimeOffset?` | when the delivery settled; absent while `running` |
| `Commit` | `string?` | the user's branch tip after promotion; only when `delivered` |
| `Outcome` | `DeliveryOutcome?` | `fast-forwarded` when delivered; `conflict` \| `dirty-working-tree` \| `hook-rejected` \| `branch-moved` when refused — the EXISTING enum and its existing tokens |
| `Detail` | `string?` | the refusal detail, or the suppressing decision and its subject |
| `Covers` | `required IReadOnlyList<string>` | every wave the delivery carries, in order, ending with this one |

**Write these stubs, and make them COMPILE:**

- `src/Guardrails.Core/Journal/WaveDeliveredRecord.cs` — a `sealed record WaveDeliveredRecord` with the
  seven members above, each getter throwing `NotImplementedException` (an `init { }` accessor is fine),
  and `public enum WaveDeliveryStatus { Running, Delivered, Refused, Suppressed }` beside it. An enum is
  data and cannot throw; its MISSING JSON TOKENS are what keep `EveryStatusToken_RoundTrips` red until
  task 10 registers them.
- `WaveJournalEntry.Delivered` in `src/Guardrails.Core/Journal/JournalModel.cs` — a WORKING nullable
  property,
  `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public WaveDeliveredRecord? Delivered { get; init; }`,
  the exact shape of the `Entry` and `Exit` markers beside it.
- `RunJournal.RecordWaveDelivery(string waveDir, WaveDeliveredRecord record)` in
  `src/Guardrails.Core/Journal/RunJournal.cs` — throwing.

**Pin these behaviours to these EXACT method names:**

- `Delivered_RoundTripsThroughTheJournalJson` — serialize AND deserialize a wave entry carrying a settled
  record through `JournalJson.Options`: every member is equal after the round trip, and the JSON carries
  `"delivered"`, `"startedAt"` and `"covers"`. Rejects a write-side-only converter — the very next
  journal write is a read-modify-write, so it kills the run after paying for the whole DAG (#625, shipped
  here once already) — and wire names that drift from the SSOT.
- `Delivered_CarriesAtCommitAndCovers` — a `delivered` record carries `at`, `commit`,
  `outcome: fast-forwarded` and its `covers` list, in order.
- `EveryStatusToken_RoundTrips` — each of the four statuses writes exactly `"running"`, `"delivered"`,
  `"refused"` or `"suppressed"`, and reads back to the same member. Rejects System.Text.Json's default
  numeric enum output and a converter that only writes.
- `RecordWaveDelivery_ReplacesTheRecordAndPersists` — a real `RunJournal` in a temp directory: record the
  wave's entry marker (`RecordWaveEntry`), then a `running` record, then a `delivered` record for the
  same wave; re-read `run.json` from disk. Exactly the `delivered` record is there, and the wave's
  `status` and `entry` marker are untouched. Rejects appending a second record, never persisting, and
  building a fresh `WaveJournalEntry` that drops the markers (the shape `ResetWaveToPending` uses on
  purpose, and this method must not).
- `AWaveEntryWithoutADelivery_OmitsTheKey` — a wave entry that never set `Delivered` serializes with NO
  `"delivered"` key, and reads back `null`. Absent, not null: the report tells a *held* wave from one
  *not reached* by it (§4).

`AWaveEntryWithoutADelivery_OmitsTheKey` is exempt from the red census: `Delivered` is a working container
in the stub, so a correct test of it is green on arrival. It must still exist, and task 10's forward
census requires it Passed. Do NOT couple it to the stubbed record to force a red.

**Who writes the record around a real delivery is not this task.** Tasks 28/29 pin and wire the
running-then-settled write, `covers`, and the `WaveDelivered` event against the real Scheduler. These
tests are the record, its wire form, and its one write path.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

The tests MUST COMPILE, and the other four MUST FAIL. Do NOT implement the record, its tokens, or the
write.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveredJournalTests.cs`, `src/Guardrails.Core/Journal/WaveDeliveredRecord.cs`, `src/Guardrails.Core/Journal/JournalModel.cs`, and `src/Guardrails.Core/Journal/RunJournal.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
