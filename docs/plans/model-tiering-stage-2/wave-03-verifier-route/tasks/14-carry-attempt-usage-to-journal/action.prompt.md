## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/14-carry-attempt-usage-to-journal`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/14-carry-attempt-usage-to-journal": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Close **#475**. `AttemptRecord.Usage` exists in the journal schema, is READ by the per-tier spend
aggregation, and is assigned by **none** of its twelve construction sites. The tokens axis of
#230-lite — the measurement this whole epic rests on — aggregates a field that is always null in
production, and its degradation path is exercised only by synthetic journals in unit tests.

Wave 2 shipped that 14/14 green. It was a recorded, deliberate deferral, not an oversight; this task
is where it gets paid.

### The chain — TWO hops are missing, not one

```
ClaudeStreamParser -> ClaudeResult.Usage -> ClaudePromptRunner -> PromptResult.Usage
                                                                        |
                                                        [MISSING] ActionRun.Usage
                                                                        |
                                                        [MISSING] AttemptRecord.Usage
```

Everything left of `PromptResult.Usage` already works. `ActionRun` carries `CostUsd` and **not**
`Usage` — grep `ActionRunner.cs` for `CostUsd = result.CostUsd` and you will see the exact line your
first hop sits beside.

### Mirror `CostUsd`, member for member. It is not merely *a* precedent — it is the same datum shape

`CostUsd` and `Usage` are siblings: `JournalTierSpend.Add(AttemptRecord)` reads them one after the
other, both answering "what did this attempt cost". `CostUsd` already reaches `run.json` on both
paths. Follow it to every site:

1. **`ActionRunner.cs`** — `ActionRun` gains `Usage`, set from `result.Usage` beside `CostUsd`.
2. **`TaskExecutor.cs`** — pass `action.Usage` to the journaller alongside the cost.
3. **`AttemptJournaler.cs`** — set `Usage` on the `AttemptRecord` it builds, **and on the
   `PendingAttempt`**.
4. **`RunReport.cs`** — `PendingAttempt` gains `Usage`. **This is the hop that makes or breaks the
   task.**
5. **`Scheduler.cs`** — the settle record adds `Usage = pending.Usage`, beside the existing
   `CostUsd = pending.CostUsd`.

### The trap — the obvious one-line fix is WRONG, and it fails silently

Setting `Usage` in `AttemptJournaler` and stopping there is the fix everyone reaches for. It works in
serial mode and **drops the value in worktree mode, which is the default**:
`Scheduler.RecordSucceededSettle` builds its OWN `AttemptRecord` from a `PendingAttempt` and never
consults the journaller. `CostUsd` survives that path only because it is on `PendingAttempt` too.
Steps 4 and 5 are what make this real; steps 1–3 alone produce numbers that are right in every unit
test and wrong in every production run — strictly worse than today's honest null, because the
per-tier spend report would silently under-count instead of reporting nothing.

Your conformance clause asserts **both** paths for exactly this reason.

### Do not move the field

`Usage` stays on `AttemptRecord`. Wave 3 put the JUDGE object on `AttemptProvenance` (D32) because it
had no other carrier; `Usage` has one — `CostUsd`'s. Moving it would break
`JournalTierSpend.Add` and change a schema wave 2 already documented, for no gain.

**Scope boundary (harness-enforced):** Write only to the five paths in this task's `writeScope`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside them —
including `JournalModel.cs` (`AttemptUsage` and `AttemptRecord.Usage` already exist; you need no
schema change), `JournalTierSpend.cs` (its consumer is already correct and starts working the moment
the field is populated), the conformance tests, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry.

`Scheduler.cs` runs every task of every plan in this repo. Add one member assignment beside
`CostUsd`; do not restructure a method around it.
