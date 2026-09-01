## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-stamp-the-pin-worktree": { "someKey": "someValue" } }`.
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

This task implements stage 5 of `docs/plans/32-executed-definition-hash.md`. **Read sections 4.2, 4.3,
4.4, 6.2 and 15.2 in full.** Where this prompt and the plan disagree, the plan is authoritative and you
should say so in your summary.

**This is the stage the whole issue is really about.** Section 4.2: *"W2 is the one that matters most and
the one the issue does not name."* #556 cites the serial settle; plan 28's motivating overnight run - the
one that made this defect worth a plan - was a **worktree-mode** run.

## Task, part 1 of 2 - the two worktree write sites

| Site | Member | Change |
|---|---|---|
| **W2** | `Scheduler.SettleAsync` | the deferred settle. **The default for a real run.** It stamps the journal entry **and** the `Guardrails-Task-Hash:` trailer. `TaskDefinitionHash.Compute(task)` becomes `task.DefinitionHashAtLoad`, in place. |
| **W3** | `Scheduler.SettleGreenIfWorktreeAsync` | the non-deferred worktree path - the trailer only, since the executor has already journaled. The same substitution. |

**Stage 3 put `DefinitionHashAtLoad` on `TaskNode` as a nullable, init-only auto-property populated
eagerly by `PlanLoader.LoadTask`; stage 5's other half promotes `LivePlanEditWatch.IsEditorArtifact` to
`internal static`. This describes the state at plan-authoring time, before either had actually run -
verify both before assuming the shape.** Find every member by **symbol**; the line numbers in the plan
are an authoring-time snapshot and this file is ~4,000 lines.

The trailer chain is `SettleAsync` → `handle.DefinitionHash` → `GitWorktreeProvider` → `TrailerMessage`.
Nothing in that chain changes: only the value handed to `handle.DefinitionHash` does. **Zero plumbing** -
both members already hold the `TaskNode` (section 5.2).

**No fallback.** Not `?? TaskDefinitionHash.Compute(task)`, not a null-guard that recomputes, not a
helper one frame away. Section 5.2 calls that *"the cheapest wrong implementation of this entire plan"*.

### EVERY OTHER `TaskDefinitionHash.Compute` CALL IN THIS FILE STAYS EXACTLY WHERE IT IS

`Scheduler.cs` holds **six** call sites. Two are the writes above. The other **four are READS and must
keep recomputing from current disk**:

| Member | Why it stays on disk |
|---|---|
| `DetectDefinitionDrift` | the resume drift pre-pass - the whole point of the comparison is that this side is current disk |
| `BuildResolvedTasks` | the Part C audit rows describe the tree as it is now |
| `ConsumePendingAnswers` | #361's answer-file anti-stale key |
| `ClassifyTaskGateAsync` | the escalation record's binding - a **durable write of a DISK value, deliberately** (section 4.4) |

Section 11 puts this first among the things an unattended run of this plan must not do:

> **No task may pin the READ sites.** Pinning R1 would make P1 pass and silence definition drift
> entirely - a strictly worse product than today.

Section 4.4 explains the fourth one, because it is the least obvious: #361's answer binding requires the
answer's hash to equal both the escalation record's **and** the unit's **current** hash at consumption.
**Both sides read disk and must stay on the same side** - pinning the stamping half alone would make a
legitimate answer fail its own binding after any mid-run edit, while pinning both would compare a pin
against a pin and check nothing.

## Task, part 2 of 2 - give the ignore predicate its one home

`LivePlanEditWatch.IsEditorArtifact` is **`private static`**. Promote it to **`internal static`**. That
is the entire change: no move, no new file, no behaviour change to the watch, and **do not edit the
ignore list itself** (`.DS_Store`, `Thumbs.db`, `*.swp`, `*.orig`, `*.rej`).

Section 15.2 explains why this small thing is in this row. Section 6.2 requires the settle-time
divergence gate (stage 13) and the watch to share **one** ignore predicate *"so a future addition cannot
reach one and miss the other."* `IsEditorArtifact` had no legal home: `HashText` and `TaskDefinitionFiles`
are forbidden by section 11, and so is a new source file. Every pressure pointed at the same escape -
**skip the ignore list** - which silently un-decides section 6.2, the sharpest call in the document. This
stage already owns the other half of that seam (`Scheduler.cs`), so the row stays deliverable by one task.

### Row 5 has ZERO margin - do not tidy it

Section 15.2, measured against the real check: `Scheduler.cs` is owned by tasks {5, 9, 13};
`LivePlanEditWatch.cs` by **{5} alone**; the intersection is exactly **{5}**. Every other row in section
15 tolerates a scope edit; this one does not. If this stage loses either `writeScope` entry, the row
splits, `GR2069` fires, and what it would be reporting is real: the two halves of one seam handed to two
tasks, with the ignore predicate on one side of the boundary and its only consumer on the other.

## What turns green

`tests/Guardrails.Integration.Tests/MidRunDefinitionEditTests.cs` (stage 7): **P2** goes from red to
green. **P3, P6a and P6b were green before and must stay green** - P6a and P6b are the entire behavioural
defence against the catastrophic wrong fix (pinning the reads).

`tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs` line 209 also goes green here - stage 2
inverted it to `Assert.Equal(hashAtStart, recorded)` precisely because this stage makes the recorded hash
the load-time pin. That file still carries other legitimate red until stage 13, so your guardrail does
not run it.

**Do NOT edit any test file.** They are outside your `writeScope`; if an assertion looks wrong, say so
with `needsHuman`.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs` and
`src/Guardrails.Core/Execution/LivePlanEditWatch.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths - including `HashText.cs`,
`TaskDefinitionFiles.cs`, `TaskNode.cs`, any test file, and the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
