## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-wire-the-plan-edit-watch": { "someKey": "someValue" } }`.
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

This task implements the SECOND HALF of stage 8 of `docs/plans/31-unattended-run-hardening.md` - the
wiring. The watch ITSELF is task 08's and has already landed. **READ SECTIONS 5.1 THROUGH 5.5 IN
FULL.** Section 5.3 is a CORRECTION to an earlier revision, and section 5.4's token analysis is what
makes the whole thing outcome-inert; neither survives summarising. Where this prompt and the plan
disagree, the plan is authoritative and you should say so in your summary.

Read: **sections 5.1-5.5**, and **section 8's `#545 part 3` bullets**.

## Your scope, and the line numbers you will find

Four files, each edit small and precisely specified. The discovery-heavy part - the definition-surface
baseline, the ignore list, the `Poll`/`Rebaseline` semantics - landed with task 08, which is why this
task is not the turn-heavy sink the original single-row stage 8 was.

**Every line number the plan quotes for `Scheduler.cs` has moved.** Task 03 landed changes to that same
file before you, and this prompt was written before either had run. Locate every seam by SYMBOL - grep
for the method name - and treat any "here is how X currently works" claim in the plan or in this prompt
as **plan-authoring-time state to verify, not settled fact**.

`LivePlanEditWatch` is implemented and its unit suite is green. **Verify that before building on it**;
if `Poll()` still throws, task 08 did not land and this is a `needsHuman`, not something to fix here.

## Task

Make `tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs` pass - P1 through P5 - without
editing it.

### 1. Construct the watch INSIDE the Scheduler - not at the composition root

**`SchedulerFactory.cs` is OUTSIDE your `writeScope`, and this is the one place the house rule points
the wrong way.** That file states the convention that the Scheduler's collaborators are constructed at
the composition root, and all twelve of them are - so the reflex is to add a thirteenth there and pass
it in. Do **not**: that is an out-of-scope write, and it fails this task on attempt one. Construct
`LivePlanEditWatch` **inside the Scheduler**, from the `PlanDefinition` the Scheduler already holds.
Nothing depends on the seam being injectable here - `ProductionWiringTests.cs` asserts only on
`GitWorktreeProvider` and the re-verifier, so internal construction breaks no existing wiring test, and
the watch has no substitutable behaviour any test needs to fake.

### 2. The two poll sites - `Scheduler.cs`

`Poll()` is called by the **Scheduler**, on the scheduler's own thread, at two boundaries that already
exist: **task dispatch** and **task settle**. **No new thread, no lock, no daemon.**

**No `FileSystemWatcher`, and this is a decision, not an omission**: it would fire on the harness's own
writes under the plan folder, needs a debounce policy, and is platform-quirky. Polling costs at most
`2N` recomputes of the definition surface per run - a few hundred KB of reads against a run that spends
dollars per attempt. The price is timeliness: the warning appears at the next scheduler boundary, not
instantly, and a single long task retrying alone can delay it by one attempt. That is accepted (section
11 risk 3).

### 3. The five harness writers - which is SIX call sites, not five

The Scheduler calls **`Rebaseline()` - plan-wide, NO task ids** - after each of:

1. a **JIT wave breakdown attempt** (`WaveBreakdownInvoker`);
2. **`BreakdownInventory.Revert`** (moves attempt-created files to `rejected/` and restores pre-existing
   ones from snapshot);
3. **`SweepIncompleteTrailingTaskFolders`** - **and this one fires from TWO places.** Plan §5.3, §13
   and the list above all read as though it were one, and it is not:
   - **`Scheduler.cs:1484`** - the post-invoke sweep, the obvious one;
   - **`Scheduler.cs:1999`** - the cancel/fault cleanup inside `LeaveWaveLoadable`.
   Re-baselining only after the first leaves the **fault path blind**: a cancelled or faulted wave
   sweeps task folders into `rejected/tasks/`, the watch sees its own harness's deletions on the next
   `Poll()`, and reports them to the operator as edits they did not make. That is a mechanism failing
   silently in the direction that *looks fine* - the exact defect class this whole plan exists to
   close. **Locate both by symbol** (the line numbers have moved); wire both.
4. **`Scheduler.QuarantineWholeTasksFolder`** (moves a wave's ENTIRE `tasks/` directory to
   `rejected/tasks`, with a catch branch that hard-deletes it recursively);
5. a **`TryResolveDrift` that RESOLVED** - and note it is **not** pre-DAG on a waved plan:
   `TryResolveDrift` has one call site, inside `DrainAsync`, which the wave loop calls **once per
   wave**, so its destructive `git reset --hard` fires mid-run.

**The rule is one `Rebaseline()` per CALL SITE, not per writer.** Count the sites, not the names.

**Plan-wide, not per-task**, because three of the five have authority over files outside the unit they
nominally act on - so a per-task re-baseline would leave the watch reporting the harness's own writes
as operator edits, and an advisory that fires on the harness's own writes stops being read.

**Only the JIT-breakdown writer has a pin (P2). The other four writers - across FIVE further call
sites - are unpinned in plan 31 and unguarded here** - no mechanical check can bind a `Rebaseline` call to the writer it is meant to follow, because
all five writer symbols already appear in `Scheduler.cs` for their own reasons. Getting them right is
on you, and a reviewer will read them. Do not treat a green guardrail as evidence all SIX call sites landed.

**Say what this is: a workaround for #557, not a fix.** Re-baselining plan-wide is only necessary
because `WaveBreakdownInvoker` has plan-wide write authority it should not have - a Claude subprocess
with `Write`/`Edit`/`Bash` at `acceptEdits`, rooted at the plan directory, with no containment hook.
Until #557 lands, the watch pays for that reach by going blind to any operator edit landing in the same
window as a JIT breakdown - a real, accepted hole caused by a hole in a different feature. Do not try to
close it here.

**Do NOT add an overwatcher hook.** Section 5.3: `Overwatch` extracts only the two `Allowlist` levers
and returns an in-memory decision; `FileEdit`/`TaskFieldEdit`/`Denylist` have no apply path in v1
(grep `OverwatchFixClassifier.cs` for "v1-inert"). A sixth hook there would be dead code, and the pin
written against it was deleted for testing an unreachable state.

### 4. The two tokens - `DecisionEntry.cs`

There is **no new `IRunObserver` event.** `DecisionRecorded(DecisionEntry)` already exists, is rendered
by **both** operator surfaces (`ConsoleRunObserver` for `--no-ui`, `LiveRunObserver` for the live
table) and forwarded by **both** transparent decorators (`OnTheFlyLogSiteObserver`,
`OnTheFlyDiagramObserver`). A new event would have to be added to all five, and `IRunObserver`'s members
carry **default no-op bodies**: a decorator missed in the wiring would compile, pass every test that
does not exercise it, and drop the warning **silently** - this plan's own failure archetype, shipped
inside the fix for it. **Touch no observer and no decorator.**

Add two token constants beside their neighbours:

| Field | Value |
|---|---|
| `boundary` | **`plan-edit`** - additive alongside `drift` / `wave` / `task` |
| `decision` | **`observed`** - the harness noticed and reported; nothing was decided and nothing changed |
| `policy` | the run's `autonomyPolicy` in force, like every other entry |
| `subject` | the edited task ids, comma-joined (the `drift` entry's own convention) |
| `headline` | **REQUIRED** - see below |
| `detail` | the per-file added / removed / modified list |

**`DecisionEntry.Headline` is `required`.** The record declares `Boundary`, `Policy`, `Decision`,
`Subject` and `Headline` all `required`; an entry that omits it does not compile (CS9035). Consider a
`PlanEditDecisions.Observed(...)` factory beside the existing `DriftDecisions` factories in that same
file.

**Why not reuse `boundary: "drift"`:** a consumer filtering on it would start counting observations as
drift decisions - the drift boundary means *a gate was reached and resolved*, and nothing here was
resolved.

**Outcome-inertness, and the reason is the `decision` token and not the boundary.**
`RunOutcomePolicy` is the only consumer that branches on a decision, via `SuppressesDelivery`
(`proceeded-best-guess` / `proceeded-unreviewed`) and `ProceededUnreviewedWaveCount`
(`proceeded-unreviewed`). **Neither reads `Boundary` at all** - verified: zero hits for `Boundary` in
that file. `observed` is neither token, so a `plan-edit` entry cannot suppress `mergeOnSuccess` and
cannot reach `ExitCodes.ProceededUnreviewed` (P3). Do not add a `Boundary` branch there;
`RunOutcomePolicy.cs` is outside your `writeScope` precisely so this stays true.

### 5. `RunReport.Observations` - `RunReport.cs`

`RunReport.Decision` is **singular** (`DecisionEntry?`) and means *the pre-DAG drift decision this run
took*. A run can produce **N** plan-edit observations. Rather than widen that field - which would touch
the shipped drift renderer for a reason unrelated to drift - add a sibling:

```csharp
public IReadOnlyList<DecisionEntry> Observations { get; init; } = [];
```

Additive and defaulted, so no existing consumer changes. The split is meaningful rather than
convenient: `Decision` is something the harness **decided**, `Observations` are things it **noticed**.

### 6. The end-of-run rendering - `RunCommand.cs`

The rendered text must state all three section 5.1 consequences and **overstate none**. "Your edit was
ignored" is FALSE - action prompts and guardrail scripts ARE re-read per attempt. Section 5.4 carries
the exact shape; follow it. P5 asserts all three on the string:

- **what your edit reaches**: this task's action prompt and guardrail scripts are re-read on every
  attempt, so an edit to either applies from the next attempt onward;
- **what it does NOT reach**: `task.json` (`writeScope`, `dependsOn`, retries, `maxTurns`) and the DAG
  were loaded when this run started; edits to those apply only to a later run;
- **the quiet consequence**: this task will record the POST-edit definition hash when it settles, so a
  later resume will not flag it as drift. That false green is **out of scope** here (filed as #556);
  this plan warns about it and does not fix it.

Also state plainly that **nothing was halted and nothing was re-run**. Halting would destroy the exact
workflow this exists to support - fixing a defective guardrail while the rest of the DAG runs.

**Section 11 risk 7, so you do not engineer around it:** `PlanLoader` validates `action.path` for
existence only, so N tasks may legitimately share one action script; editing it mid-run reports **N**
edited tasks, one per sharer. That is literally correct and is accepted. Do NOT de-duplicate by file -
that would hide which tasks are affected, the fact the operator needs. Group by file in the RENDERING
when one file appears under several task ids.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/Scheduler.cs`, `src/Guardrails.Core/Execution/DecisionEntry.cs`,
`src/Guardrails.Core/Execution/RunReport.cs` and `src/Guardrails.Cli/Commands/RunCommand.cs`. After this
task completes, the harness runs a `git diff` check and rejects any edit outside these paths -
including `SchedulerFactory.cs` (construct the watch inside the Scheduler instead - see section 1),
`LivePlanEditWatch.cs` (task 08s), `HashText.cs`, `TaskDefinitionFiles.cs`,
`RunOutcomePolicy.cs`, any observer or decorator, any test file, and the `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry. Do NOT edit the authored tests: make them pass by
fixing the implementation, and if a test is genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` to the state-out path and stop.
