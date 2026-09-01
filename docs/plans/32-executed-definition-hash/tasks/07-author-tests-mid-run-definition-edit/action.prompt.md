## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-author-tests-mid-run-definition-edit": { "someKey": "someValue" } }`.
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

This task implements stage 7 of `docs/plans/32-executed-definition-hash.md`. **Read sections 4.2, 4.3,
5.8 (P2, P3, P6a, P6b) and 8 in full.** Where this prompt and the plan disagree, the plan is
authoritative and you should say so in your summary.

## Why this stage runs BEFORE stage 5, and it is a deliberate change to the plan's ordering

Section 15 sequences this stage after stage 5. **The DAG here runs it before**, and the reason is P2's
whole purpose. Section 5.8:

> **P2 - every write site, not just the serial one.** Without this, an implementation that fixes
> `AttemptJournaler.cs` alone passes the issue's own pin while leaving the default execution mode broken.

Authored *after* stage 5, P2 is green on arrival and grades nothing. Authored here - with stage 4's
serial sites already pinned and stage 5's worktree sites not - it is **RED**, and stage 5's forward
`tests-pass` on this class becomes a real gate on *"W2 is the one that matters most and the one the issue
does not name."* Nothing about section 15's `writeScope` table changes; only the edge does.

## Task

Create **`tests/Guardrails.Integration.Tests/MidRunDefinitionEditTests.cs`**.

- Namespace **`Guardrails.Integration.Tests`** (flat - every file in that project uses it, including
  those in subfolders).
- Class **`MidRunDefinitionEditTests`** - **pinned; the guardrails filter on it**. `public sealed class`.
- Follow the shipped `PlanEditedDuringRunTests.cs` for the fixture idiom: `IClassFixture<HostRepoCleanlinessGuard>`
  and a private `TempGitRepo` helper. **`TempGitRepo` is not a shared fixture in this repo** - it is a
  `private sealed class` copy-pasted into ~32 Integration test files. Copy the idiom into your own file,
  as the house style does; do not try to extract a shared one (that is a different change, and every
  other file is outside your `writeScope`).

Four `[Fact]`s, with these **EXACT** method names:

| Pin | Method name | Behaviour |
|---|---|---|
| **P2** | `TheRecordedHash_IsThePreEditPin_WhenTaskJsonIsEditedMidRun_Worktree` | The serial pin (stage 1's P1) asserted again in **worktree mode** - write sites **W2** (`Scheduler.SettleAsync`, the deferred settle that is **the default for a real run**) and **W3** (`SettleGreenIfWorktreeAsync`). Compute the hash before the run, edit `tasks/<id>/task.json` mid-run, assert the journal's recorded hash equals the **pre-edit** value. **RED today**, because stage 4 fixed only the serial sites. |
| **P3** | `TheTrailerAgreesWithTheJournal_OnARealGitSegment` | The `Guardrails-Task-Hash:` trailer on the task's integration commit equals the journal's recorded hash, asserted on a **real git segment**. This is what keeps Part C's rule-3 trailer corroboration sound. **DECLARED EXEMPTION** - see below. |
| **P6a** | `TheDriftPrePass_SeesThePostEditHash_WithoutAReload` | Section 5.8's **respecified** P6. Load a plan, capture the pin, mutate `task.json` on disk, then invoke the drift pre-pass **without re-loading**. It must see the **post-edit** hash. This is a direct assertion that the READ site recomputes, and it is the only form that separates a pinned read site from a disk one at all. **DECLARED EXEMPTION.** |
| **P6b** | `AnEarlierRunsSettledTask_StillHaltsOnDrift_WhenEditedAfterThisRunsLoad` | The reachable production shape, on a **waved, two-run** fixture: a task in wave N settled green in a **previous** run, whose definition is edited **after this run's load** and before wave N's drain. Its pin and its recorded hash are both the pre-edit value, so a pinned read site sees a match and waves it through while a disk read halts. **DECLARED EXEMPTION.** |

### P6 was respecified because the obvious form was a TAUTOLOGY - do not reintroduce it

An earlier draft asked for *"after a between-runs edit, the resume still halts with `DefinitionDrift`."*
An adversarial pass showed that **passes with the read sites fully pinned**: a between-runs edit is on
disk *before* run N+1's load, so the pin computed at that load already equals the post-edit bytes, the
pre-pass mismatches against the *recorded* hash either way, and the substitution is unobservable. The pin
called *"the single most important in this plan"* could not fail the implementation it exists to fail.
**P6a and P6b replace it, and the plan needs both.**

### P6b's earlier form was UNSATISFIABLE - do not write that one either

An earlier draft asked for drift on *an earlier wave's* settled task within one run. That cannot happen:
`DrainAsync` is called per wave with **that wave's tasks only**, and `DetectDefinitionDrift` iterates
exactly that list, so nothing re-checks an earlier wave within one run. The two-run fixture above is the
shape that is both reachable and discriminating.

### Three DECLARED EXEMPTIONS, and why they are not dropped rows

P3, P6a and P6b assert properties that are **true today and must stay true** - they are the *"nothing
else moved"* half of milestone A, not defect pins:

- **P3**: today both the trailer and the journal are stamped from the same settle-time recompute, so they
  already agree. After stage 5 both are stamped from the same pin, so they still agree. A **CORRECT** test
  is GREEN on today's tree, and demanding red would demand a correct implementation fail.
- **P6a / P6b**: the READ sites recompute from disk today and must **keep** doing so. Section 11: *"No
  task may pin the READ sites. Pinning R1 would make P1 pass and silence definition drift entirely - a
  strictly worse product than today."* These two are what make that implementation fail; they are green
  before and after by construction.

Guardrail 02 asserts those three rows **executed** (present in the runner's result file, not `[Skip]`ped)
rather than **failed**. They stay IN the manifest: a dropped row and an oversight look identical from the
outside. **Three of four exempt is high, and it is honest** - this file carries one defect pin and three
regression pins, which is what section 5.8 asks for. If you find yourself wanting a fourth exemption, P2
has stopped being red and that is a finding, not a row to add: escalate with `needsHuman`.

### Sequencing the mid-run edit deterministically

Plan 31 already shipped the mechanism: `tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs`'s
`CreateMidRunEditPlan` builds a two-task plan whose first task's action WRITES into the second task's
folder, so the edit is sequenced by the **DAG** rather than by a timer. **Read it before inventing
anything**, and reuse the mechanism in your own file rather than inventing a second one (section 8).
`PlanEditedDuringRunTests.cs` is outside your `writeScope` - copy from it, never edit it.

**Do NOT make the edit conditional, retimed, or removed to reach green.** The edit IS the fixture
(section 11): a task that "stabilizes a flaky timing test" by deleting the thing under test has deleted
the plan.

### Do NOT

- Do NOT assert P2 in serial mode. Section 8: *"a design that proved this only in serial mode would have
  proved it in the mode plan 28 did not use."* The default for a real run is worktree mode.
- Do NOT weaken P2 into "the hash is non-null" or "the hash changed" - both are true with the defect
  intact.
- Do NOT touch any file outside the one named below.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/MidRunDefinitionEditTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path - including `PlanEditedDuringRunTests.cs`,
production files, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.
If you hit a compile error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
