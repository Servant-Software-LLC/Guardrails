## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `13-author-tests-route-warmth`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "13-author-tests-route-warmth": { "someKey": "someValue" } }`.
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

## READ THIS FIRST: the plan does not define "warm"

Section 3.4 of `docs/plans/30-telemetry-phase-1.md` lists **"warm/cold"** among the charter's original
items. That is the whole of it. **The plan names the flag and does not DEFINE it** — there is no
paragraph, table or sentence anywhere in the plan of record that says what makes an attempt warm.

**The definition below is the BREAKDOWN's choice, not the plan's, and a maintainer may replace it.**
Do not present it in your summary as something the plan supplied, and do not go looking for a
plan passage that states it — there is none, and inventing a citation for it would be worse than
having none. Say in your summary that you implemented the breakdown's definition and that it is open
to replacement.

**The definition this task pins:**

> An attempt is **COLD** when it is the first attempt **in this run** to resolve a given
> `(runner, model)` pair. It is **WARM** on every later attempt that resolves the same pair.
> It is **`null`** when no route resolved at all — a script action — because *"not applicable"* is
> not *"cold"*.

Two things that definition deliberately settles, and the reason for each:

1. **The grain is `(runner, model)`, not the runner alone.** Two models served by one runner are two
   different first invocations; the second model's first attempt pays the same first-call price the
   first model's did. Section 3.4's neighbouring item — the unified-memory one — is the same argument
   one level up: the same model name is a different thing on a different box, so the identity of a
   route is what was actually resolved, never the process that served it.
2. **`null` is a third value, not a synonym for `false`.** `AttemptProvenance.RouteWarm` is `bool?`
   for exactly this reason (task `03-extend-the-journal-record-shape` declared it nullable and its
   doc comment says so). A script action invokes no model, so calling it "cold" would put a zero
   into a column an analysis averages, and the corpus would report a first-invocation penalty for
   work that invoked nothing.

If you believe a different definition is the right one, do **not** implement it. Write
`{"needsHuman": {"question": "<the alternative and why>", "kind": "blocked-work"}}` to the state-out
path and stop — the whole point of pinning it here is that the next task implements exactly what these
tests assert, and a definition changed on one side of that pair is a silent divergence.

## Plan of record

This task authors the failing tests for the **warm/cold** item of section 3.4 of
`docs/plans/30-telemetry-phase-1.md`. Read section 3.4 in full; where this prompt and the plan
disagree, the plan is authoritative and you should say so in your summary. On this one point the plan
is SILENT rather than in disagreement, which is what the section above is about.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Provenance-on-failed-attempts already reaches the journal; do not touch it. This task rides that
shipped mechanism rather than adding one.

## What already exists when this task runs

`03-extend-the-journal-record-shape` has already added:

```csharp
// on Guardrails.Core.Journal.AttemptProvenance
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public bool? RouteWarm { get; init; }
```

So **these tests COMPILE against the tree they run on.** Nothing populates the member yet — that is
`14-record-whether-the-route-was-warm`'s job — so a correct test that asks for a value gets `null`
and goes RED at runtime. Compiling is required; being red is the point. If a test does not compile,
that is a mistake to fix, not the intended TDD red.

## Where the value will come from (authoring-time state — verify it before you rely on it)

Everything in this section describes the tree **as it stood when this prompt was written**. Tasks
`10-fold-the-digest-into-the-provenance`, `12-record-the-turn-count` and
`12a-segment-the-attempt-durations` all edit `TaskExecutor.cs`, and any of them may land before this
task runs, so **grep for the markers named here; never trust a line number, and re-read what you find
before asserting on it.**

- **`TaskExecutor.BuildProvenance`** — grep for `BuildProvenance(`. A `private` instance method on
  `TaskExecutor` with the signature
  `private Journal.AttemptProvenance? BuildProvenance(TaskNode task, WorktreeHandle worktree, TierResolution? route)`.
  It is where every launch-time provenance fact is set, and it is where warmth will be set.
- It returns `null` early when the route named no model **and** the worktree is not a real git
  segment. Grep for `IsRealGitSegment` to read that condition as it now stands. A serial script
  attempt therefore has **no provenance object at all**, which is a different fact from "a provenance
  whose `RouteWarm` is null" — your `AScriptActionWithNoRoute_RecordsNoWarmth` test must be written
  so it is satisfied by either shape, and must say in a comment which one it observed.
- **The route** is `Guardrails.Core.Prompts.TierResolution` — a `public sealed record` with
  `RunnerName`, `Model`, `Runner`, `Tier`, `Effort` — constructible from a test with an object
  initializer.
- **The model as recorded** is `PromptExecutionSupport.ResolvedModelForDisplay(route.Model)`
  (`src/Guardrails.Core/Execution/PromptExecutionSupport.cs`), which maps a null model to the
  `(cli default)` sentinel. Two routes that both name no model resolve to the SAME recorded model,
  and your tests should treat them as the same route.
- **`WorktreeHandle`** is a `public sealed class` with `init` properties that all default to `""`,
  so `new WorktreeHandle()` is a non-real segment.
- **`TaskExecutor` is `public sealed`** and is constructible from Core.Tests today — see
  `tests/Guardrails.Core.Tests/Journal/ExecutedDefinitionHashTests.cs` (grep for `new TaskExecutor(`)
  for the six-argument form and the collaborators it needs.

`BuildProvenance` is **private**. Reaching it by reflection is the mechanism that compiles today, and
it has a house precedent — grep `BindingFlags.NonPublic` in `tests/Guardrails.Core.Tests` and read
`TopologyM0BookkeepingTests.cs`. Use it, or any other mechanism that satisfies both constraints:

- the file **COMPILES** against the tree as it now stands, and
- the three behavioural tests are **RED** because nothing sets the flag yet.

If you use reflection on `BuildProvenance`, **name the method in a comment as a pinned dependency**:
the next task is told not to rename it, and your comment is what makes that promise legible.

## The tests to author

One file, and only this file:
`tests/Guardrails.Core.Tests/Execution/RouteWarmthTests.cs`.

Class **`RouteWarmthTests`**, `public sealed`, in namespace `Guardrails.Core.Tests.Execution`,
carrying `[Trait("Category", "ModelEvidence")]` on the class — the convention every shipped telemetry
suite in this project uses (see `tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs`).

Encode **exactly these five behaviours**, each as a `[Fact]` with **exactly the method name given**.
The names are pinned because this task's guardrail binds each behaviour to its method name in the
runner's TRX; a differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | the FIRST attempt in this run to resolve a given `(runner, model)` pair records `RouteWarm = false` | `TheFirstAttemptOnARoute_IsCold` |
| 2 | a LATER attempt resolving the SAME `(runner, model)` pair records `RouteWarm = true` | `ASecondAttemptOnTheSameRoute_IsWarm` |
| 3 | the same runner with a DIFFERENT model is a different route, so its first attempt is cold again — the pair is the identity, not the runner | `ADifferentModelOnTheSameRunner_IsColdAgain` |
| 4 | a script action, which resolves no route at all, records NO warmth — `null`, never `false` | `AScriptActionWithNoRoute_RecordsNoWarmth` |
| 5 | `RouteWarm` is declared on `AttemptProvenance` and NOT on `AttemptRecord` — **by reflection** | `WarmthRidesTheProvenance_SoItReachesBothSettlePaths` |

### On behaviour 5, which is the one that matters most

Assert, by reflection, that `RouteWarm` is declared on `Guardrails.Core.Journal.AttemptProvenance` and
is **not** declared on `Guardrails.Core.Journal.AttemptRecord`.

The reason is mechanical, and `src/Guardrails.Core/Journal/JournalModel.cs` documents it in the doc
comment on `AttemptProvenance.Judge` (grep for `A member hung directly off the attempt record` — the
sentence you want is in that block). `AttemptRecord.Provenance` is the only member that already rides
`PendingAttempt`, so it is the only member that reaches BOTH record-construction paths: the serial
`AttemptJournaler` and `Scheduler.RecordSucceededSettle`, which is the DEFAULT worktree mode. **A
member hung directly off `AttemptRecord` lands in serial mode and silently vanishes in worktree
mode.** Warmth rides the provenance so it reaches both paths for free, and this test is what stops a
later refactor moving it.

This is the same shape and the same citation as `09-author-tests-digest-reaches-the-provenance`'s
`TheDigestRidesTheProvenance_SoItReachesBothSettlePaths`. Write it the same way.

### Two of these five are GREEN when you finish, and that is correct

- **`AScriptActionWithNoRoute_RecordsNoWarmth`** — nothing sets `RouteWarm` today, so a script
  attempt's warmth is already absent. A correct test passes.
- **`WarmthRidesTheProvenance_SoItReachesBothSettlePaths`** — task 03 already declared `RouteWarm` on
  `AttemptProvenance` and nowhere else, so a correct reflection test passes.

Their guardrail row declares each as an exemption and asserts only that it **RAN**. Do **not** "fix"
either into failing, and do **not** mark either `[Fact(Skip=…)]` — a skipped exemption is no coverage
at all. They exist because the next task edits the site they constrain: behaviour 4 is what stops
warmth being written as `false` for script work, and behaviour 5 is what stops it being moved onto
the record where worktree mode would drop it.

The other three must be **RED**. Each must actually obtain a provenance and assert on its `RouteWarm`
value. A test that constructs an `AttemptProvenance` itself and asserts something about the object it
just built is hollow: it passes today, it passes forever, and this task's guardrail will name it.

### Concurrency, stated so the tests do not encode a wrong assumption

One `TaskExecutor` serves the whole run and parallel workers call into it concurrently (grep
`_executor.ExecuteAsync` in `src/Guardrails.Core/Execution/Scheduler.cs`). Under parallelism, WHICH of
two simultaneous first attempts on one route is the cold one is a race, and that is acceptable. The
invariant that must hold — and the one your tests should assert — is that **exactly one attempt per
`(runner, model)` pair per run is recorded cold.** Write behaviours 1-3 sequentially against a single
executor; do not write a test that asserts a particular ordering under concurrency.

**Do NOT implement the flag.** `src/Guardrails.Core/Execution/TaskExecutor.cs` is outside this task's
writeScope and belongs to `14-record-whether-the-route-was-warm`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Execution/RouteWarmthTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path — including changes to production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the
state-out path and stop.
