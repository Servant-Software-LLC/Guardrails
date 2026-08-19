## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/12-record-advisory-into-judge-provenance`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/12-record-advisory-into-judge-provenance": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make the DoR §6.5 **verifier advisory** arrive in `run.json`. Tasks 09/10 built `VerifierAdvisory`
as a unit; nothing calls it yet. This is the **JIT boundary** half of §6.5's de-duplication ruling:

> the JIT re-check records `judge.advisory` in that attempt's provenance **ALWAYS**.

### What to change

In `src/Guardrails.Core/Execution/GuardrailRunner.cs`, at the point where task 07 resolves the judge:

1. Call `VerifierAdvisory` with the actor route and the resolved judge to get the finding (or none).
2. Put it on the **`Advisory`** member of the judge datum task 08 already carries to the journal.

That is the whole change. The datum's path to `run.json` is already built and already proven by task
08's guardrails — you are filling a field on an object that already makes the trip. **Do not build a
second carry.**

### Two things this must NOT do

- **It must never halt, fail, or degrade the run.** Advisory means advisory (§6.5, and D26's
  "degrade what is advisory; halt what is load-bearing"). A judge weaker than its actor is a
  *finding*, not an error: the guardrail still runs, on the block that was resolved. If computing
  the advisory throws, the attempt must still proceed — an advisory that can break a run is strictly
  worse than no advisory.
- **It must not emit ANY log line, and it must not try to compare against the preflight.** Task 13
  owns the run-start surface. The §6.5 "log only when the observed finding differs from what the
  preflight predicted" behaviour is **not built in this wave**: `GuardrailRunner`'s only output
  channel is the observer, `IRunObserver.cs` is not in your scope, and nothing carries task 13's
  run-start prediction down to the JIT boundary — there is no left-hand side to compare against.
  `VerifierAdvisory` exposes the decision as a unit (tasks 09/10 test it); wiring it is a later
  wave's work. **Compute and record. That is all.** Do not chase the log line into a needs-human. §6.5's de-duplication ruling is
  precisely that the two surfaces say different things: run-start emits one line per affected task,
  the JIT boundary **records silently into provenance** and only *logs* when what it observes
  differs from what the preflight predicted. `VerifierAdvisory` already exposes that decision — call
  it; do not re-derive the rule here.

### The revalidate path — a null actor route

Task 07 passes a **null** actor route on the `RevalidateAsync` call site (there is no action, so
there is no actor). With no actor there is no "weaker than" relation, so **there is no advisory
condition to find**: record the judge with no `advisory`. Do not invent a comparison, and do not
treat a null route as an error — a revalidate still resolves a real judge, and task 08 records it.

### Absent, never null

A judge with no advisory condition records **no `advisory` key at all**, exactly as the schema (task
04) requires — not an empty string and not a null. "Recorded always" means the advisory is *computed*
on every judge resolution, not that a key is emitted for every judge.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/GuardrailRunner.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including `VerifierAdvisory.cs` (task 10
owns it; if its API does not fit, that is a `needsHuman`, not a redesign), `TaskExecutor.cs`,
`JournalModel.cs`, `Scheduler.cs`, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry.
