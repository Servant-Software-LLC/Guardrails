## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `06-implement-retry-loop-escalation`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "06-implement-retry-loop-escalation": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "06-implement-retry-loop-escalation": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Wire the escalation ladder into `TaskExecutor`'s retry loop so that every test in
`tests/Guardrails.Core.Tests/Escalation/RetryLoopEscalationTests.cs` passes.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/TaskExecutor.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including the test file, `EscalationLadder.cs`,
`JournalModel.cs`, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. **Do NOT edit the authored tests.** Make them pass by fixing the implementation. If the authored
tests are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather
than changing them.

Read `tests/Guardrails.Core.Tests/Escalation/RetryLoopEscalationTests.cs` first — it is the
specification — and `src/Guardrails.Core/Prompts/EscalationLadder.cs`, which already contains the whole
rung-selection decision. **Nothing about which rung to climb to belongs in this file.** Your job is
three mechanical edits inside `TaskExecutor.cs`.

### Follow the shape the file already has — MEASURE it, do not take this on trust

The retry loop already carries two per-task counters of exactly this shape, and the ladder is a third.
**Grep for them rather than reading this prompt's description of them:**

```
grep -n "maxTurnsRetries" src/Guardrails.Core/Execution/TaskExecutor.cs
```

At authoring time that returned **5** hits, which trace one counter end to end: it is DECLARED beside
`timeoutRetries` before the `for (int attemptIndex = ...)` loop, INCREMENTED in an
`if (attempt.Outcome is AttemptOutcome.MaxTurns)` block just after `last = attempt.Result;`, PASSED as
an argument to `RunAttemptAsync`, DECLARED as a parameter there, and USED. If your grep returns a
different number or a different shape, **trust the grep**, follow what it found, and say so in your
summary — this description was accurate when the plan was authored and the file moves.

Cite the code by its durable markers, not by line number: `grep` for
`int budget = 1 + (task.Retries` (the budget), for `if (attempt.Outcome is AttemptOutcome.Timeout)`
(where the sibling counters increment), and for `TierResolution? route = ResolveRoute(task);` (where
the route is resolved). Line numbers in this file move.

### The three edits

1. **Count guardrail-failed attempts.** Add a per-task counter beside `timeoutRetries` and
   `maxTurnsRetries`, incremented in the same place they are — a sibling `if` on
   `AttemptOutcome.GuardrailFailed`, immediately after `last = attempt.Result;`.
   `grep -c "AttemptOutcome.GuardrailFailed" src/Guardrails.Core/Execution/TaskExecutor.cs` returned
   **6** at authoring time; you want the retry-loop OUTCOME test, not the sites that CONSTRUCT a
   guardrail-failed attempt result. Trust the grep over this sentence.
2. **Thread it to route resolution.** Pass the counter into `RunAttemptAsync` exactly as
   `timeoutRetries` and `maxTurnsRetries` are passed, and apply the ladder where the route is
   resolved — `ResolveRoute` is the ONE §6 attempt-launch resolution, called once per attempt, and its
   own XML doc already names the ladder as the thing that slots in there without moving the seam.
   `EscalationLadder.Apply(config, route, escalations)` returns the route unchanged when `escalations`
   is 0, when the route resolved no rung, and when the ladder is capped — so a plan that never fails a
   guardrail, and every single-runner plan, gets byte-identical behaviour.
3. **Record it.** `BuildProvenance` already reads the route it was handed and never recomputes one;
   add `EscalatedFrom = route?.EscalatedFrom` alongside the existing `Tier` and `TierSource` lines, so
   `run.json` and `attempt-provenance.json` both carry it. `TierProvenance.SourceFor` already turns an
   escalated route into `TierSource.Escalated`, so `TierSource` needs no change here.

### The trigger set is CLOSED, and narrowing it is the point

Escalation is triggered by **`guardrail-failed` only** — never by a timeout, a max-turns stop, a
transient pause, or a permission wall. Those already have their own counters and their own remedies
(a longer clock, a bigger turn budget, a bounded pause), and a guardrail failure is the one outcome
that indicts the MODEL's work rather than the infrastructure around it. Escalating on a timeout would
spend a frontier model on an infrastructure problem and would read in the report as *"this task was too
hard"* when nothing of the sort was measured. There is a test in this pair whose only job is to prove
a timeout does not escalate.

Two further invariants the tests pin, both easy to break by accident:

- **The retry budget is unchanged.** An escalated attempt draws from the SAME pool: each guardrail
  failure climbs one rung, total attempts stay `1 + (task.Retries ?? DefaultRetries)`, there is no
  budget reset and no new cumulative cap. Do not touch `budget`, `grantedRetriesTotal`, or
  `MaxCumulativeGrantedRetries`.
- **`feedbackPath` still reaches the escalated attempt.** It is passed to the next attempt
  independently of which model runs it, and that independence is why escalating costs nothing: an
  escalated attempt gets a stronger model **and** the #179 failure detail. Do not move, gate, or
  reorder the `feedbackPath = attempt.FeedbackPath;` assignment.

Leave the `#174`/`#182` no-op short-circuit, the `#264` deterministic-script short-circuit, and the
`#269` overwatcher consults exactly as they are: they settle needs-human BEFORE the next attempt would
launch, so a task they stop never escalates, which is correct — escalation never converts an honest
halt into a silent pass.
