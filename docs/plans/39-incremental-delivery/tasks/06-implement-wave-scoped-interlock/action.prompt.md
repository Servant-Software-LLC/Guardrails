## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `06-implement-wave-scoped-interlock`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-implement-wave-scoped-interlock": { "someKey": "someValue" } }`.
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

**RECORD the wave on the decision; do NOT parse it out of `Subject` (review, 2026-09-11).**
§1a claimed `decisions[]` already carried a wave attribution. It does not — `DecisionEntry` has
no wave member, and `Subject` is free text whose meaning varies by `Boundary`: a `wave` entry puts a
wave dir there, a `task` entry (including the needs-human gate's `proceeded-best-guess`) puts a task
id, and a `drift` entry puts a comma-joined list of task ids. So the interlock must read a recorded fact
rather than reverse-engineer one from a string.

**Task 05 stubs the `Wave` member. Populate it at the two sites that create a SUPPRESSING decision**,
both in `src/Guardrails.Core/Execution/Scheduler.cs`. Search for `DecisionTokens.ProceededUnreviewed`
and `DecisionTokens.ProceededBestGuess`; line numbers move.

- `RecordProceededUnreviewed`, the review gate's `proceeded-unreviewed` entry: `Wave` is its `waveDir`.
- The below-threshold `proceeded-best-guess` entry in `ActOnJudgmentCallAsync`, reached through
  `ClassifyAndActAsync` from two kinds of caller:
  - The wave checkpoint passes `boundary: "wave"` with the wave's directory as `subject`, so `Wave` is
    that directory.
  - The task gates in `ClassifyTaskGateAsync` pass `boundary: "task"` with a task id, so `Wave` is that
    task's `TaskNode.WaveDir`, which is null for a flat plan.

  Pass the wave down as a value from the caller that holds the node; never recover it by splitting
  `subject`.

**Every other decision creator leaves `Wave` null.** That covers:
- the drift, reset, breakdown and plan-edit factories in `DecisionEntry.cs`;
- the creators in `Overwatch.cs`, `FileEscalationSink.cs`, `AnswerFileConsumer.cs`, `RunCommand.cs`
  and `RunReset.cs`.

None of them records a suppressing decision, the interlock reads only suppressing decisions, and all but
`DecisionEntry.cs` are outside your scope. A null `Wave` on a suppressing decision fails closed (see
below), so a site you miss holds deliveries rather than leaking one.
`WaveScopedInterlockTests.TheSchedulersProceededUnreviewedDecision_RecordsItsWave` drives the first site
through the real `Scheduler`.

Make `WaveScopedInterlockTests` pass: the interlock is scoped to the waves a delivery CARRIES, rather than
evaluated once for the run.

**Implement the set-based entry point task 05 stubbed in `RunOutcomePolicy.cs` (review round 4,
`d39-interlock-ride-along`):**

```csharp
public static DecisionEntry? SuppressingDecisionForDelivery(
    IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves)

public static bool SuppressesDelivery(
    IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
    SuppressingDecisionForDelivery(decisions, coveredWaves) is not null;
```

- `SuppressingDecisionForDelivery` returns the FIRST `proceeded-best-guess` / `proceeded-unreviewed`
  decision whose `Wave` is in `coveredWaves`, or whose `Wave` is null — a suppressing decision recorded
  outside any wave holds every delivery, so the check fails closed — else null.
- The bool overload is defined in terms of the entry, exactly like the existing run-scoped pair, so the
  answer and its evidence cannot drift apart (#597).
- A delivery covers every wave since the previous delivery point, this one included (§1b). So a held
  wave's work riding along holds the later delivery, and a wave whose work already reached the user's
  branch is not in the set.
- Task 08 calls it at a delivering wave's barrier FIRST, before any trial merge is built, so a delivery
  the interlock holds never runs the user's hooks. Task 29 later derives `coveredWaves` from the journal
  so the set survives a resume. Leave the single-argument `SuppressesDelivery` / `SuppressingDecision`
  untouched: the run-end interlock still uses them.

**Find the current call yourself** — search with the Grep tool for `SuppressingDecision` and
`MergeOnSuccessForcedByOperator` rather than trusting a line number; `Scheduler.cs` has moved under
several plans.

The operator override (`--merge-on-success`) must still lift it, and must still be unreachable from a
manifest key — that asymmetry is deliberate (#597) and this change must not quietly restore the
manifest path. That is the run-end call in `Finalize`; leave it as it is. Do not add a barrier call site
or a barrier override here: the barrier delivery, and the delivery predicate it shares with `Finalize`,
belong to task 08.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/DecisionEntry.cs`, `src/Guardrails.Core/Execution/RunOutcomePolicy.cs`, and `src/Guardrails.Core/Execution/Scheduler.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
