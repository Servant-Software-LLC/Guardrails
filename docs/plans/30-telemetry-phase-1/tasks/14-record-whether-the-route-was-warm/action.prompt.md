## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `14-record-whether-the-route-was-warm`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "14-record-whether-the-route-was-warm": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements the **warm/cold** item of section 3.4 of
`docs/plans/30-telemetry-phase-1.md`. Read section 3.4; where this prompt and the plan disagree, the
plan is authoritative and you should say so in your summary.

**On this one point the plan is SILENT rather than authoritative.** Section 3.4 names "warm/cold" and
does not define it. The definition below is the BREAKDOWN's choice, pinned by
`13-author-tests-route-warmth` and asserted by the tests you must turn green. A maintainer may replace
it later; you must not replace it here.

> An attempt is **COLD** when it is the first attempt **in this run** to resolve a given
> `(runner, model)` pair. It is **WARM** on every later attempt that resolves the same pair.
> It is **`null`** when no route resolved at all — a script action — because *"not applicable"* is
> not *"cold"*.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Provenance already reaches the journal on failed attempts as well as successful ones; you are adding a
field to a mechanism that works, not building one.

## Task

Set `AttemptProvenance.RouteWarm` in `src/Guardrails.Core/Execution/TaskExecutor.cs` so
`tests/Guardrails.Core.Tests/Execution/RouteWarmthTests.cs` goes green.

## Where to make the change (authoring-time state — VERIFY IT before you rely on it)

Everything in this section describes the tree **as it stood when this prompt was written**. Tasks
`10-fold-the-digest-into-the-provenance`, `12-record-the-turn-count` and
`12a-segment-the-attempt-durations` all edit this same file **before** this task runs, so **every line
number in this file's history is stale on arrival. Grep for the markers below, read what you find, and
correct this prompt in your summary if the shape has moved.**

### The site: `BuildProvenance`

Grep for **`BuildProvenance(`**. As authored, it is a `private` instance method:

```
private Journal.AttemptProvenance? BuildProvenance(TaskNode task, WorktreeHandle worktree, TierResolution? route)
```

It builds the launch-time provenance object and is the one place every route-derived fact is set
(`Model`, `Runner`, `Kind`, `Tier`, `TierSource`, `Effort`). Warmth is a route-derived fact, so it
belongs in the same initializer.

**Do NOT rename `BuildProvenance`, and do NOT change its signature.** The authored tests reach it by
reflection because it is private; a rename turns every one of them into a runtime null-reference and
the failure will read as a test problem rather than as your rename. If you believe the signature must
change, escalate with `kind: "blocked-work"` instead.

### The two facts that make up the route key

- The runner is `route?.RunnerName` — grep for `RunnerName` inside `BuildProvenance`.
- The model as RECORDED is `PromptExecutionSupport.ResolvedModelForDisplay(route.Model)` — grep for
  `ResolvedModelForDisplay`. It maps a null model to the `(cli default)` sentinel, so two routes that
  both name no model are the SAME route. **Key on the recorded form, not on the raw `route.Model`**,
  or two attempts that ran on the identical resolved model would be counted as two different first
  invocations.

`BuildProvenance` already computes that value into a local (grep for `string? model =` at the top of
the method). Reuse it rather than recomputing.

### The early return, and why warmth must be null-safe around it

Grep for **`IsRealGitSegment`**. As authored, `BuildProvenance` returns `null` outright when the route
named no model **and** the worktree is not a real git segment — so a serial script attempt has no
provenance object at all. In worktree mode a script attempt DOES get a provenance object whose `Model`
is null.

Both shapes must record **no warmth**: `null`, never `false`. `RouteWarm` is `bool?` for exactly this
reason (read the doc comment `03-extend-the-journal-record-shape` put on it). A script action invokes
no model, so calling it cold would put a zero into a column an analysis averages, and the corpus would
report a first-invocation penalty for work that invoked nothing. The pinned test
`AScriptActionWithNoRoute_RecordsNoWarmth` is green today and must STAY green — if your change makes
it red, you wrote `false` where the answer is "not applicable".

### The set lives on the executor, and it is touched concurrently

Hold the run-scoped set of already-invoked routes as a field on `TaskExecutor`.

**One `TaskExecutor` instance serves the whole run, and parallel workers call into it concurrently** —
grep `_executor.ExecuteAsync` in `src/Guardrails.Core/Execution/Scheduler.cs` (two call sites, one of
them the parallel worker loop), and `SchedulerFactory.CreateExecutor`, which builds exactly one.

That makes a plain `HashSet<string>` **wrong in two ways at once**: it is not thread-safe, and a
check-then-add would let two simultaneous first attempts on one route both observe "not present" and
both record COLD — a corpus that says the same route was invoked for the first time twice.

Use a primitive whose first-writer-wins is **atomic**: `ConcurrentDictionary<string, byte>.TryAdd`
returns `true` exactly once per key, so `bool cold = _invokedRoutes.TryAdd(key, 0);` and
`RouteWarm = !cold` is the whole decision. Compare route keys with `StringComparer.Ordinal`, as this
codebase does everywhere else it keys on an identifier.

Under parallelism, WHICH of two simultaneous first attempts on one route is the cold one is a race,
and that is acceptable. The invariant is that **exactly one attempt per `(runner, model)` pair per run
is recorded cold.**

### Warmth is recorded once per attempt, at build time

Do not "fold" warmth later the way `10-fold-the-digest-into-the-provenance` folds the observed model
(grep for `ObservedModel is { } observedModel` to see that fold). The observed model is a
POST-LAUNCH fact — the runner only reports what it ran on once it has run. Warmth is known the moment
the route resolves, and folding it later would move the `TryAdd` after the action, which is both
pointless and a second place for the race to be got wrong.

## Do not do these

- **Do NOT edit the tests.** `tests/Guardrails.Core.Tests/Execution/RouteWarmthTests.cs` is outside
  this task's writeScope; an edit there fails the write-scope check and burns a retry. If a test is
  genuinely wrong or incompatible with the pinned definition, write
  `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to the state-out path.
- **Do NOT put `RouteWarm` on `AttemptRecord`.** It rides `AttemptProvenance` because
  `AttemptRecord.Provenance` is the only member that already rides `PendingAttempt` and therefore
  reaches BOTH settle paths — the serial `AttemptJournaler` AND `Scheduler.RecordSucceededSettle`,
  which is the DEFAULT worktree mode. `src/Guardrails.Core/Journal/JournalModel.cs` documents that
  failure; grep for `A member hung directly off the attempt record`. The pinned test
  `WarmthRidesTheProvenance_SoItReachesBothSettlePaths` asserts this by reflection and is green today
  — if your change makes it red, you moved the member.
- **Do NOT persist the set across runs.** Warmth is a WITHIN-RUN fact. A cache on disk would make the
  first attempt of a fresh run read warm, which is exactly backwards.

## Scope boundary (harness-enforced)

Write only to `src/Guardrails.Core/Execution/TaskExecutor.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path — including changes to other production
files, the authored test file, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry.
