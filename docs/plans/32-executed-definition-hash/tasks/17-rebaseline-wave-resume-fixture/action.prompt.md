## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "17-rebaseline-wave-resume-fixture": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements stage 17 of `docs/plans/32-executed-definition-hash.md` - a row added after a run
halted at stage 13. **Read sections 4.1, 6.2, 6.3 and 15.1.** Where this prompt and the plan disagree,
the plan is authoritative and you should say so in your summary.

## Why this stage exists

Stage 13's gate is correct and its own four guardrails are green - but it turns **two shipped Core tests**
red, and §15.1's ledger never accounted for them. Causality was proven, not inferred: with the single
`CheckExecutedDefinitionDivergence` call disabled, `SchedulerWaveExecutionTests` is **14/14**; with it
enabled, exactly these two fail.

Both fixtures model a **resume** as a *second scheduler run over the same in-memory `PlanDefinition`*:

```csharp
PlanDefinition plan = b.Load().Plan! with { };     // loaded ONCE, before the edit
// ... run 1 ...
File.WriteAllText(<a guardrail script>, "# edited");
// ... run 2, reusing `plan` (or a `with { Config = ... }` clone of it) ...
```

Those `TaskNode`s carry pins captured **before** the edit, so the gate reports a divergence - **correctly**.
**In production a resume is a fresh process**: `PlanLoader` re-reads the folder, the pin *is* the edited
bytes, and the gate is silent. The fixture models something production never does.

**The gate cannot be narrowed instead**, and this was checked before the row was added: the shape is
byte-for-byte §6.7's **P15 row 2** - a divergence that must still be reported *after* the plan-edit watch
has already reported and re-baselined on the edit - which stage 10 authors and stage 13's own guardrail 02
enforces. Re-baselining at Scheduler construction kills P15 row 2; re-baselining at dispatch kills row 1;
both are the disk fallback §5.2 forbids outright. The fixture is what is wrong, not the gate.

## Task

Two methods in **`tests/Guardrails.Core.Tests/SchedulerWaveExecutionTests.cs`**. In each, **run 2 obtains
its plan from its own `b.Load().Plan!` call**, after the on-disk edit - exactly as a real resume does.

| Line | Method | Change |
|---|---|---|
| ~191 | `WaveDrift_CompletedWaveChanged_AutoPolicy_RewindsAndReRuns_WithWaveBoundaryDecision` | `autoPlan` becomes a **fresh load** carrying the same config clone: `b.Load().Plan! with { Config = ... with { AutonomyPolicy = AutonomyPolicy.Auto } }` instead of `plan with { Config = ... }` |
| ~250 | `PendingFutureWaveEdit_IsNotDrift_RunsNormally` | run 2's plan becomes a **fresh** `b.Load().Plan!` instead of reusing `plan` |

Find both by **method name**; the line numbers are an authoring-time snapshot.

`WavePlanBuilder.Load()` is `new PlanLoader().Load(PlanDir)` - a pure re-read of the folder, with no
rebuild and no side effects, so calling it twice is safe. `b.Load().Plan!` is the **house idiom**: it
appears **14 times** in this file already. You are adding the 15th and 16th.

Run 1 keeps its own load. Do not hoist a shared one; the whole point is that the two runs see the folder
at two different moments, which is what a resume actually is.

## What must NOT change

- **Every assertion, untouched.** Both methods keep asserting their original product claims - an
  auto-policy wave rewind re-runs the drifted wave and stays green; an edit to an all-pending future wave
  is not drift. They now assert those claims against the semantics milestone A actually ships. Measured
  today: **4** assertions in the first method, **3** in the second; guardrail 02 holds both counts.
- **The `File.WriteAllText` edit stays.** It IS the fixture - the same rule §11 states for every timing
  fixture in this plan. A task that "stabilizes a flaky test" by removing the thing under test has deleted
  the point of it.
- **Delete nothing, skip nothing, narrow nothing.** The file's `[Fact]` count is **14** before and after.
- **Touch no other method.** The other twelve are unaffected and are outside the change.

## What "green" means here, and why your strong guardrail is a source-shape check

Both tests pass **today** (stage 13 has not landed) and pass **after** your fix. So a `tests-pass` at this
stage certifies nothing - it is green either way, and guardrail 03 is labelled a regression clause for
exactly that reason. **The behavioural difference only exists once stage 13 lands, and stage 13
`dependsOn` you.** At the moment this task runs there is no runtime signal to assert on, so the
load-bearing check is guardrail 02, on the fixture's *shape*. That is the honest reason, and it is the
one place in this plan where a source-shape check outranks a test because the test cannot yet exist.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/SchedulerWaveExecutionTests.cs`. After this task completes, the harness runs
a `git diff` check and rejects any edit outside that path - including `src/**`, other test files, and the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
