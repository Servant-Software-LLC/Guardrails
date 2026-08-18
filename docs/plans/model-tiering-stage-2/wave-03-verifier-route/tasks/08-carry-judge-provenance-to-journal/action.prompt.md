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

Carry the resolved judge datum from `GuardrailRunner` all the way to `run.json`, so DoR §12.4's judge
provenance is a field something actually writes.

**This task exists because wave 2 shipped a schema member nothing populated** (#475): the datum was
mined correctly, reached one hop short of the journal, and the field stayed null forever. Do not
repeat it — the deliverable is the value ARRIVING, not a member existing.

### The path — traced, not guessed

`FailedGuardrails` is the sibling datum that already makes this exact trip. Follow it:

```
GuardrailRunner  ->  GuardrailRunResult  ->  TaskExecutor  ->  AttemptJournaler  ->  AttemptRecord
```

Grep for `FailedGuardrails` across `src/Guardrails.Core/Execution/` and put the judge datum on the
same surfaces at the same sites. Task 06 has already exposed it on the result `GuardrailRunner`
returns.

**There are TWO AttemptRecord construction paths and BOTH must carry it.** Miss the second and judge
provenance lands in serial runs and silently vanishes in worktree runs — which is the default:

1. **`AttemptJournaler`** — grep for where it assigns `FailedGuardrails`; there are several call
   sites, including the succeeded path (`CompleteSucceededOrInvalidFragment`).
2. **`Scheduler.RecordSucceededSettle`** — builds its **own** `AttemptRecord` from a `PendingAttempt`,
   bypassing `AttemptJournaler` entirely. If `PendingAttempt` cannot carry the datum, extending it is
   part of this task, not a reason to skip the path.

**Absent, never null.** A script attempt, and a task whose guardrails are all deterministic, have no
judge — their records must omit the key entirely, exactly as the schema (task 04) requires.

### Scope

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/GuardrailRunner.cs`,
`src/Guardrails.Core/Execution/TaskExecutor.cs`,
`src/Guardrails.Core/Execution/AttemptJournaler.cs` and
`src/Guardrails.Core/Execution/Scheduler.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside those paths — including `JournalModel.cs` (tasks 03/04
own the schema), the conformance tests, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry.

`Scheduler.cs` is the most load-bearing file in the harness — it runs every task of every plan. Add
your field beside the ones already there; **do not restructure a method around it.** A regression
here fails every downstream wave and every other plan in this repo.
