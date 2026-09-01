## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "11-author-tests-delivery-gate": { "someKey": "someValue" } }`.
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

This task implements stage 11 of `docs/plans/32-executed-definition-hash.md`. **Read sections 6.4, 6.5,
6.6, 6.7 (P9, P11, P13) and 8 in full.** Where this prompt and the plan disagree, the plan is
authoritative and you should say so in your summary.

## Task

Create **`tests/Guardrails.Integration.Tests/DivergenceDeliveryGateTests.cs`**.

- Namespace **`Guardrails.Integration.Tests`** (flat).
- Class **`DivergenceDeliveryGateTests`** - **pinned; the guardrails filter on it**. `public sealed class`.
- Use `IClassFixture<HostRepoCleanlinessGuard>` and a private `TempGitRepo` helper, following the shipped
  `PlanEditedDuringRunTests.cs`. **`TempGitRepo` is not a shared fixture in this repo** - it is a
  `private sealed class` copy-pasted into ~32 Integration files. Copy the idiom; do not extract one.
- Reuse `PlanEditedDuringRunTests`' `CreateMidRunEditPlan` **mechanism** in your own file - a two-task plan
  whose first task's action writes into the second task's folder, so the edit is sequenced by the **DAG**
  rather than by a timer (section 8). That file is outside your `writeScope`: copy from it, never edit it.

Three `[Fact]`s, with these **EXACT** method names:

| Pin | Method name | Behaviour |
|---|---|---|
| **P9** | `AGreenRunWithAMidRunDefinitionEdit_DoesNotDeliver_AndExitsTwo` | **Milestone C's acceptance criterion. RED today.** A run with a mid-run `task.json` edit, `mergeOnSuccess` **ON**, every task green: **nothing is merged to the user's branch**, the plan branch retains the work, exit code is **2**. Section 6.7: *"An implementation that passes every other bullet and still merges has not fixed the reported defect."* Assert on **all three**: the user's branch did not move, the plan branch has the commits, and the exit code is 2. |
| **P11** | `TheInRunDivergenceAndTheNextResumesDrift_NameTheSameTaskSet` | **RED today.** Run to the divergence halt, then run `guardrails run <folder>` **again** and assert the resume's `DefinitionDrift` report names the **same task ids** as the in-run divergence report. Section 6.6: *"C is A's finding delivered one run earlier"* - the gate carries no remediation vocabulary of its own, and an implementation in which the two disagree about the set is wrong. |
| **P13** | `AfterADivergenceHalt_TheWorkSurvivesOnThePlanBranch` | The diverged task's integration commit is on the plan branch and its journal entry reads `succeeded`. Nothing is discarded, and the branch stays Part-C-corroborable. **DECLARED EXEMPTION.** |

### Plus the two §6.5 corrections, asserted inside P9

Both are consequences of the one added `AllSucceeded` term that need **work**, not acceptance:

1. **The terminal plan-guardrail gate must report NOT EVALUATED, never PASSED.** `RunCommand`'s
   `planGuardrailsPassed` is `!report.AllSucceeded || await PlanGuardrailPhase.EvaluateAsync(...)`, so a
   divergence run does not merely skip the terminal gate - it **records that the gate passed**. Assert the
   durable record says *not evaluated*.
2. **`run.json`'s delivery reason must not say the run was not wholly green** when its `tasks{}` shows
   every task `succeeded`. `RunCommand.DescribeDelivery` would write exactly that self-contradiction. That
   record exists (#542) so an unattended pipeline with no console has a machine-readable answer; a wrong
   one is worse than none.

Both are stage 15's to implement, and both are red until then - which is correct.

### P13's exemption

Today a mid-run-edited run goes green, so the commit and the journal entry are both there and a **CORRECT**
test is **GREEN**. Its job is to **stay** green after stage 13. It is the pin standing against the form of
candidate (3) the issue itself proposed: §6.4 re-specifies *"refuse to record a success"* as **record the
success, block the delivery**, because refusing discards paid work (#554's defect, fixed hours before this
plan was written) **and** leaves a plan-branch commit whose journal says otherwise - precisely the
present-but-uncorroborated state Part C rule 3 refuses to rewind past, turning a recoverable drift into a
mandatory full `guardrails reset -y`. The census asserts P13 **executed**; write it, do not skip it.

### Worktree mode is not a detail

Section 8: these pins *"cannot be faked - #382's lesson is that a fake-masked unit guardrail certifies
green while the real composition-root path is broken, and the default execution mode for a real run is
worktree mode."* Drive a **real** run over a **real** git repo. A P9 asserted against a fake worktree
provider proves nothing about the seam that actually delivers.

### NAME NO API MEMBER THIS PLAN HAS NOT WRITTEN YET

`RunReport.ExecutedDefinitionDivergence` (stage 13) does not exist. Everything above is observable without
it: the CLI **exit code**, whether the user's branch moved, what is on the plan branch, and what the
journal file says. Guardrail 01 enforces this; `src/**` is outside your `writeScope`.

**Do NOT make the mid-run edit conditional, retimed, or removed to reach green.** The edit is the fixture.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/DivergenceDeliveryGateTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path - including `PlanEditedDuringRunTests.cs`,
`MidRunDefinitionEditTests.cs`, production files, and the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file - rewrite the assertion against what exists today, or write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
