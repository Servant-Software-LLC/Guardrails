## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/08-carry-judge-provenance-to-journal`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/08-carry-judge-provenance-to-journal": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Carry the resolved judge datum from `GuardrailRunner` to `run.json`, so DoR §12.4's judge provenance
is a field something actually writes.

**This task exists because wave 2 shipped a schema member nothing populated** (#475): `AttemptRecord.Usage`
is declared, is READ by the per-tier spend aggregation, and is assigned by **none** of its twelve
construction sites. Every guardrail was green. The deliverable here is the value ARRIVING, not a
member existing.

### The path — traced against the real tree, not guessed

```
GuardrailRunner -> GuardrailRunResult.Judge -> TaskExecutor (fold into `provenance`) -> both record paths
```

Task 07 has already exposed the resolved judge on `GuardrailRunResult`. Your job is the fold:

In `TaskExecutor.RunAttemptAsync`, the attempt's provenance is built BEFORE the action runs — grep
for `BuildProvenance(task, worktree, route)`. After the guardrail call returns, fold the judge into
that same object with a `with` expression before the journaller is called:

```csharp
provenance = provenance is null ? null : provenance with { Judge = /* from the guardrail result */ };
```

**Why this is the whole job — and why `Scheduler.cs` is NOT in your scope.** `AttemptProvenance` is
the one member that already rides `PendingAttempt`, so a value folded onto it reaches BOTH attempt
record construction paths with no further edit:

- **serial** — `AttemptJournaler` sets `Provenance = provenance` on the record it builds;
- **worktree (the DEFAULT)** — `AttemptJournaler` also sets `Provenance = provenance` on the
  `PendingAttempt`, and `Scheduler.RecordSucceededSettle` copies it straight across
  (`Provenance = pending.Provenance`). That settle record has eight members and is the reason
  placement was decided this way (D32).

This is exactly how wave 2's actor tier provenance already reaches the journal: `Tier`, `TierSource`,
`Runner`, `Kind` and `Model` all sit on `AttemptProvenance` for the same reason. Your judge object
hangs one level down from them, and the two halves of the routing story end up in one place.

**If you find yourself needing to edit `Scheduler.cs`, `RunReport.cs` or `AttemptJournaler.cs`, STOP.**
That is the signal that the datum went onto the wrong record, not that the scope is too small. Re-read
where task 03 put it. Do not work around this with a fifth file — write
`{"needsHuman": "<what you found>"}` instead. `Scheduler.cs` runs every task of every plan in this
repo; a regression there fails every other plan, and this task should not be touching it at all.

### BOTH paths that journal an attempt, not just the attempt loop

`TaskExecutor` writes attempt records from **two** methods, and the judge object belongs on both:

1. **`RunAttemptAsync`** — the normal attempt loop, described above.
2. **`RevalidateAsync`** — the re-verification a human's in-place fix runs through. It journals TWO
   `AttemptRecord`s via `_journal.RecordAttempt` (one `GuardrailFailed`, one `Succeeded`), and
   **neither sets `Provenance` at all today**. A revalidate runs the same prompt guardrails and
   resolves a judge exactly as an attempt does — so a revalidate graded by a model must say which
   model graded it, or the one path a human is actively working through is the one path with no
   record of who judged their fix.

For the revalidate records there is no launch-time provenance to fold into, so **construct one
carrying the judge** (its route-derived fields are legitimately absent — there was no action, so no
actor model, no segment, no grants). Do not skip the path because the object does not already exist;
that is the same reasoning that left `AttemptRecord.Usage` unpopulated in #475.

**Absent, never null.** A script attempt and a task whose guardrails are all deterministic have no
judge — their provenance must omit the key entirely,
exactly as the schema (task 04) requires. A judge object built out of nulls is worse than no object:
it reads as "a judge resolved and every field was empty".

### Scope

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/GuardrailRunner.cs` and
`src/Guardrails.Core/Execution/TaskExecutor.cs`. Task 07 touched both before you — your change is the
FOLD (and whatever `GuardrailRunner` must expose to make it possible), not a redo of task 07's
wiring. After this task completes, the harness runs a `git diff` check and rejects any edit outside
those two paths — including `JournalModel.cs` (tasks 03/04 own the schema), `Scheduler.cs`,
`AttemptJournaler.cs`, `RunReport.cs`, the conformance tests, or the `.csproj`. An out-of-scope edit
fails the task immediately and consumes a retry.
