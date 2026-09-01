## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "10-author-tests-divergence-record": { "someKey": "someValue" } }`.
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

This task implements stage 10 of `docs/plans/32-executed-definition-hash.md`. **Read sections 6.1, 6.2,
6.3, 6.7 (P10, P12, P15, P16) and 7 in full.** Where this prompt and the plan disagree, the plan is
authoritative and you should say so in your summary.

Milestone A makes the *next resume* honest. But **a run that goes green to completion never resumes**,
and `mergeOnSuccess` defaults ON - so for the headline scenario (an unattended overnight run, a mid-run
edit, everything ends green) milestone A alone changes nothing an operator will ever see. Milestone C is
required, not a companion.

## Task

Create **`tests/Guardrails.Core.Tests/Execution/ExecutedDefinitionDivergenceTests.cs`**.

- Namespace **`Guardrails.Core.Tests.Execution`** (mirror the sibling `Execution/` files).
- Class **`ExecutedDefinitionDivergenceTests`** - **pinned; the guardrails filter on it**. `public sealed
  class`, `IDisposable` for its temp-dir fixture.

Five `[Fact]`s, with these **EXACT** method names:

| Pin | Method name | Behaviour |
|---|---|---|
| **P12** | `AJitBreakdownWritingOutsideItsWave_Diverges_WhileOneInsideItIsSilent` | **ONE pin, TWO-SIDED.** A JIT wave breakdown writing **inside** its own wave is **silent** (the splice gives that wave fresh `TaskNode`s and therefore fresh pins); one writing **outside** its wave leaves the victim wave's pins pointing at bytes no longer on disk, so the victim task's settle **fires**. **RED today** on the firing half. |
| **P15** | `ADivergenceIsReported_EvenAfterTheWatchAlreadyReportedAndReBaselined` | The **provenance** discriminator. **RED today.** See below - this is the most important pin in the file. |
| **P10** | `AnUneditedRun_WritesNoDivergenceKeyAndNoDivergenceDecision` | An unedited run's `run.json` gains **no** new key and **no** new `decisions[]` entry - asserted on the **FULL** lists, never on the absence of one token. **DECLARED EXEMPTION.** |
| **P16** | `AStrayEditorArtifactMidRun_LeavesTheRunGreenAndDelivering` | A mid-run stray editor artifact (`.DS_Store`) under a task's `guardrails/` leaves the run **green and delivering**, while that task's **recorded** hash still differs from disk. **DECLARED EXEMPTION.** |
| **P16b** | `APreExistingEditorArtifact_LeavesTheRunGreenAndDelivering` | The **other side** of the same tripwire. An artifact that is **already present when the plan loads** - a `.DS_Store` in the checkout, a `.swp`, a `.orig` - with **nobody editing anything** during the run, leaves the run **green and delivering**. **DECLARED EXEMPTION.** |

### P15 is the pin that decides whether this plan shipped or something else did

Section 6.7, verbatim:

> Milestone C is fully satisfiable **without ever consulting `DefinitionFilesAtLoad`**: drive
> `ExecutedDefinitionDivergence` from `LivePlanEditWatch`'s already-collected `PlanEdit`s and P9 through
> P13 all pass, shipping the watch's **moving** baseline under this plan's name. **Asserting the report's
> payload is not enough** - a watch-driven implementation can populate both hash fields from the watch's
> own before/after snapshot and satisfy a payload pin exactly. The pin must discriminate on
> **provenance**: after a mid-run edit that `Poll()` has **ALREADY** reported and re-baselined on (so the
> watch holds the post-edit bytes and will never report that file again), the settling task must **still**
> diverge. Only a pinned baseline survives that.

So the fixture must **force a `Poll()` before the settle**, verify the watch reported the edit **and
adopted it**, and then assert the task still diverges at settle. `LivePlanEditWatch.Poll()` re-baselines
after reporting (`Poll()` is report-then-adopt); that adoption is what makes the discriminator work.

### P12 is ONE pin, not five - the reachability analysis is already done

Section 6.7 does it by hand, deliberately: all five harness writers - the breakdown attempt,
`BreakdownInventory.Revert`, the trailing-folder sweep, `QuarantineWholeTasksFolder`, and a resolved
`TryResolveDrift` - act at **wave boundaries**, and **none can execute between a task's dispatch and that
task's settle within a wave**. Five negative pins would be five vacuous tests, handed to an unattended
agent with the uncheckable instruction *"each must test a reachable state"*. Write the one two-sided pin.

The in-wave-silent half falls out of `SpliceAuthoredWave` replacing **only the one authored wave** - every
other wave's `WaveNode`, `TaskNode`s and pins ride through unchanged (section 7). That is not designed for;
it falls out of pinning at `TaskNode` construction, and it is what makes the negative half meaningful
rather than vacuous.

### Three DECLARED EXEMPTIONS

P10, P16 and P16b are **silence** pins: true today, and they must **stay** true. A correct test is GREEN on this
tree, so demanding red would demand a correct implementation fail. The census asserts they **executed**
(present, not `[Skip]`ped). Write them; do not skip them.

- **P10** is Risk 3's only mitigation. `AllSucceeded` gates delivery for **every** run, so a defect in the
  new term silently stops the product delivering anything. Assert on the **full** decisions list and the
  **full** `run.json` key set: plan 31 §8's lesson is that a silence pin scoped to one token passes
  trivially when the mechanism is broken.
- **P16** is §6.2's tripwire. `HashText.EnumerateFolderFiles` globs `"*"` and filters nothing, so an
  editor artifact **is** part of a task's recorded definition - and must stay that way. The **gate**
  compares the ignore-list-filtered surface; the **recorded hash** keeps the full one. Both halves in one
  test.
- **P16b** is the half P16 structurally cannot cover, and it is the **reachable** one. P16's artifact
  appears **mid-run**, so it is absent from the load-time map and present in the settle walk - an
  implementation that filters **only the settle side** passes P16 while being broken. An artifact present
  **at load** is the case that bites: filtered on one side only, its label sits in *before* and not in
  *after*, reads as a **vanished** label, and blocks delivery on a run **nobody edited**. Every trigger is
  ordinary - a `.DS_Store` already in the checkout, an operator's `.swp` from opening a guardrail to read
  it, a `.orig`/`.rej` from any pre-run git operation. Build the fixture with the artifact **already in
  the task's `guardrails/` folder before the run starts**, edit nothing, and assert the run is green and
  delivers.

### NAME NO API MEMBER THIS PLAN HAS NOT WRITTEN YET

`RunReport.ExecutedDefinitionDivergence` (stage 13) and `TaskJournalEntry.DefinitionHashAtSettle` (stage
12) do **not** exist. Assert on the **serialized artifact** instead - the `run.json` key set, and the
`decisions[]` entries' `boundary` / `decision` **strings** (`"definition-divergence"` / `"halted"`). That
is also what makes P10 a real full-list silence pin rather than a check for one absent token. Guardrail 01
enforces this mechanically; `src/**` is outside your `writeScope`.

`TaskNode.DefinitionHashAtLoad` and `DefinitionFilesAtLoad` **do** exist (stage 3) and are fair game.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Execution/ExecutedDefinitionDivergenceTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside that path - including production files,
other test files, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.
If you hit a compile error caused by a missing symbol in another file, do NOT edit that file - rewrite the
assertion against what exists today, or write `{"needsHuman": "<what is missing>"}` to the state-out path
and stop.
