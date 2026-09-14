## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `10-implement-wave-delivered-journal`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "10-implement-wave-delivered-journal": { "someKey": "someValue" } }`.
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

Fill real logic over the stubs so `WaveDeliveredJournalTests` passes. Design 39 §4 ("The record, pinned
at review").

- **`WaveDeliveredRecord`** — plain auto-properties with the members' own types; `Status`, `StartedAt`
  and `Covers` are `required`. `Outcome` is the EXISTING `DeliveryOutcome`, serialized by its existing
  converter, which gains exactly one token (below). KEEP the `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` attributes
  task 09's stub put on `At`, `Commit`, `Outcome` and `Detail`. `JournalJson.Options` writes nulls, so
  without them a `running` delivery serializes `"at": null` and three more null keys, and
  `ARunningRecord_WritesNoSettledKeys` stays red.
- **The `WaveDeliveryStatus` tokens, in `JournalJson.cs`** — `running`, `delivered`, `refused`,
  `suppressed`. Follow the file's own pattern: a static token method beside `DeliveryOutcomeToken`, and a
  converter registered in `Build()` beside `WaveStatusConverter`, handling BOTH directions and throwing on
  an unknown member. Without a converter System.Text.Json writes the enum's ORDINAL; with a write-only one
  the next read-modify-write of `run.json` throws and kills the run (#625). Check that the round-trip test
  really exercises the read path before calling this done.
- **The `trial-gate-failed` token, in `JournalJson.cs`** — for `DeliveryOutcome.TrialGateFailed`, which
  task 09 added. Add it to `DeliveryOutcomeToken` AND to `DeliveryOutcomeConverter.Read`: the converter
  throws on an unknown member in both directions, and a record that writes but cannot be read back breaks
  the next resume. `TheTrialGateFailedOutcome_RoundTrips` pins both directions. Add no other outcome:
  tasks 18/19 add `PartiallyDelivered` and its token later.
- **`RunJournal.RecordWaveDelivery(waveDir, record)`** — the house wave-write shape the methods beside it
  use: take the journal lock, `GetOrCreateWave(waveDir)`,
  `UpdateWave(waveDir, existing with { Delivered = record })`, `Persist()`. REPLACE the record, never
  append, and keep the wave's `Status`, `Entry`, `Exit`, `DefinitionHash` and `MarkerSha` exactly as they
  were — that is what separates it from `ResetWaveToPending`, which drops them on purpose. It is the
  unconditional write primitive. Restoring a prior `delivered` record when a resume finds the delivery
  already landed is task 29's Scheduler policy, so do not build it in here.

The Scheduler's calls to `RecordWaveDelivery`, and the `WaveDelivered` event, are tasks 28/29 — do not
reach into `Scheduler.cs`.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Journal/WaveDeliveredRecord.cs`, `src/Guardrails.Core/Journal/JournalModel.cs`, `src/Guardrails.Core/Journal/RunJournal.cs`, and `src/Guardrails.Core/Journal/JournalJson.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
