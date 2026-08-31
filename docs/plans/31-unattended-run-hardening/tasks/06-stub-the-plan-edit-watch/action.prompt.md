## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-stub-the-plan-edit-watch": { "someKey": "someValue" } }`.
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

This task implements stage 6 of `docs/plans/31-unattended-run-hardening.md`. Read **section 5.2**, and
**section 7's third bullet**, which is why this stage exists at all.

## Why this stage exists, and why it must stay INERT

The plan-edit watch is the one deliverable in plan 31 that **needs a stub stage**: its tests must
CONSTRUCT `LivePlanEditWatch`, and there is no way to write them against an observable artifact alone
(section 7). So this task declares the type and nothing else, and stage 7's tests are red **only
because** these members are inert. Implement any of them here and the corresponding test is green on
arrival and proves nothing for the life of the plan. That is what this task's guardrail 02 checks, and
the bans are not tidiness - they are what keeps stage 7's TDD red real.

It also catches the opposite: a declaration that is missing or misshapen makes stage 7's tests fail to
COMPILE rather than fail behaviourally - a red for the wrong reason, and one the test-author task
cannot fix because this file is outside ITS writeScope.

## Task

Create `src/Guardrails.Core/Execution/LivePlanEditWatch.cs`. Declare **exactly** the surface plan 31
Section 5.2 pins, in that one file (a second file would be outside your `writeScope`):

```csharp
public sealed record PlanEditedFile(string TaskId, string Label, PlanEditKind Kind);
public enum PlanEditKind { Added, Removed, Modified }

public sealed record PlanEdit(string TaskId, string OldHash, string NewHash,
                              IReadOnlyList<PlanEditedFile> Files);

public sealed class LivePlanEditWatch
{
    public LivePlanEditWatch(PlanDefinition plan);

    /// <summary>Recompute the definition surface, return what changed since the last call, and
    /// re-baseline. Empty when nothing changed. Never throws: an unreadable file is skipped.</summary>
    public IReadOnlyList<PlanEdit> Poll();

    /// <summary>Silently re-baseline these tasks - a HARNESS-authored edit is not an operator edit.
    /// An unknown task id is a no-op. Pass no ids to re-baseline the whole plan.</summary>
    public void Rebaseline(params string[] taskIds);
}
```

Namespace `Guardrails.Core.Execution`, matching its neighbours in that folder.

### The two rules that decide whether this stub is usable

1. **`Poll()` and `Rebaseline()` MUST throw `NotImplementedException`.** They are stage 8's
   deliverables. A body that returns `[]`, or that silently does nothing, makes stage 7's tests green
   on arrival.
2. **The CONSTRUCTOR must NOT throw `NotImplementedException`.** Stage 7's tests construct the watch
   in order to call the two methods; a throwing constructor makes every one of them fail with a
   constructor exception, which is indistinguishable from "the type is missing" and tells the stage-8
   implementer nothing. Store the `PlanDefinition` (or do nothing with it) and return. Argument
   validation - an `ArgumentNullException` - is fine and is not what the ban targets.

Carry the two XML doc comments through verbatim: they are the contract stage 7 writes tests against and
stage 8 implements, and section 5.2 pins them precisely so the three stages cannot disagree.

### Do NOT implement anything

No hashing, no `TaskDefinitionFiles.Enumerate`, no ignore list, no baseline dictionary. If you find
yourself reasoning about `HashText` or which files define a task, you are doing stage 8's work.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/LivePlanEditWatch.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path - including `Scheduler.cs`, `DecisionEntry.cs`,
any test file, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.
If you hit a compile error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
