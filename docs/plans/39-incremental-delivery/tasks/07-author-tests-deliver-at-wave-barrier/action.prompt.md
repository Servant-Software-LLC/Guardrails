## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `07-author-tests-deliver-at-wave-barrier`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-author-tests-deliver-at-wave-barrier": { "someKey": "someValue" } }`.
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

Author failing tests for the core of design 39 §1: **fire the existing delivery at each wave's
existing exit gate, instead of once at run end.**

**Test file:** `tests/Guardrails.Integration.Tests/WaveDelivery/WaveBarrierDeliveryTests.cs`
**Test class:** `WaveBarrierDeliveryTests`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Drive the REAL Scheduler over the REAL `GitWorktreeProvider`**, not an injected fake of the delivery. A
test that injects its own delivery and asserts it was called proves the fake was called — that is how a
feature ships wired to nothing and green. Assert the effect only the production path emits: the user's
branch actually carries the wave's commits. Two house patterns do this, and either is fine:

- construct the Scheduler over `new GitWorktreeProvider(repoPath, worktreeRoot)`, the way
  `WaveExecutionRunTests` does; or
- run the real `run` command in process, the way `RunOutcomeWiringTests.RunViaCliAsync` does, over a real
  git repo with `maxParallelism` of at least 2, so `SchedulerFactory` builds the worktree provider. With
  `maxParallelism: 1` the run is SERIAL: it has no provider and never delivers at a barrier, so no
  implementation could turn your tests green.

A guardrail enforces this (review 2026-09-13). The test file must construct the real provider or run the real
command. It must not:
- use `FakeWorktreeProvider`, `RecordingWorktreeProvider` or any other provider type except the real one,
  whether in code or named in a string for reflection;
- declare, alias or `DispatchProxy` a type that implements `IWorktreeProvider`;
- hand `IWorktreeProvider` to a mocking library.

A `Func<IWorktreeProvider>` helper is fine. Every row below is observable through git and through files your
fixture's own scripts write, so no provider decorator is needed. When a scenario needs a commit to land on
the user's branch mid-run, have one of the run's own task or gate scripts make it (`git -C <user repo>`,
with the path written into the script by your test).

**Two fixture rules.** The plan's final wave always delivers at run end (review round 5), so a wave that must
deliver at its barrier is never the plan's final wave. Only `TheFinalWave_DeliversAtRunEnd_NotAtItsBarrier`
gives its plan a plan-level `guardrails/` folder.

**The one thing §1 says this design must get right:** the gate must run against the tree the delivery
will PRODUCE — the user's branch with this wave's commits merged onto it — not merely the plan branch
as it stands. The plan branch also carries the run's own harness commits and anything a prior wave
left.

**The order at a delivering barrier is a TRIAL MERGE on a scratch ref — DECIDED at review (2026-09-11,
refined 2026-09-13).** An earlier version of this prompt said "perform the merge first, then gate", which
means writing to the operator's branch before anything authorizes it. That contradicts §3 (the gate is
what authorizes the delivery), contradicts #588 (`TheUsersCheckoutIsNotModified`, which task 16 pins),
and the only way back from a red gate is the un-merge §1a says the interlock CANNOT do. So, at a wave that
is a delivery point:

1. Decide first: `mergeOnSuccess` (#340) must be on, and the #361 interlock over every wave the delivery
   carries must not hold it, unless the operator passed `--merge-on-success`. A held delivery builds no
   trial at all.
2. Merge the plan branch onto the user's tip at `refs/guardrails/trial/<waveDir>`.
3. Run the wave's `Exit` gate against THAT tree. When the user's branch did not move, the trial IS the
   plan-branch tip, and the exit gate that already ran is that gate. When it moved, the gate runs again in
   a worktree checked out at the trial commit.
4. Only on green, fast-forward the user's branch to the trial commit. On red, delete the ref; the user's
   branch never moved.

Review round 5 settled three edge cases:
- the plan's final wave never delivers at its barrier and is left to the run-end delivery;
- a failure of the gate on the trial tree halts the run as an exit-gate failure whose headline names the trial
  merge;
- a trial the user's hook rejects holds this and every later barrier delivery without halting, and leaves the
  work to the run-end merge in the user's own checkout.

**The git side of that sequence is a provider primitive (review round 4,
`d39-trial-delivery-primitive`).** Task 31 ships `IWorktreeProvider.CreateTrialDelivery`,
`PromoteTrialDelivery` and `DiscardTrialDelivery` on the real `GitWorktreeProvider`, and task 30 tests them
directly: the user's git hooks run on the trial merge commit, and promotion re-checks #588 and #448 before
it fast-forwards. Task 08 wires the Scheduler to them. Your tests assert EFFECTS in git and in files your
scripts write — never that a provider member was called. Do not install a git hook in these fixtures, except in
`AHookRejectedTrial_HoldsEveryLaterBarrierDelivery`: the hook behavior of the trial itself is task 30's to pin.

**The merged-tree scenario several rows share.** A wave with `delivers: true` whose exit-gate check FAILS
when `teammate.txt` exists in the tree it runs in, plus a commit adding `teammate.txt` that lands on the
user's branch mid-run, before that wave's barrier. The plan branch never has the file, so the exit gate
passes on the plan branch and fails only on the trial merge. Have the check append one line per run to a
log file OUTSIDE the repo (an absolute path your test writes into the script): the `HEAD` sha of the tree
it ran in, and whether `teammate.txt` existed there.

**Pin these behaviors to these EXACT method names:**

- `ADeliveringWaveMergesAtItsOwnBarrier` — not at run end.
- `ANonDeliveringWaveRidesAlongToTheNextDeliveryPoint` — §1b: a delivering wave ships everything
  accumulated since the last delivery point.
- `AWaveWhoseExitGateFails_DoesNotDeliver`
- `TheGateRunsAgainstTheMergedTree_NotThePlanBranchAlone` — the §1 correctness point above, in the
  merged-tree scenario. Assert BOTH that the check's log records a run in which `teammate.txt` existed, at
  a `HEAD` that has the user's mid-run commit as an ancestor (`git merge-base --is-ancestor`), AND that the
  user's branch does not carry the wave's commits. The first half is what makes this row red today: the
  exit gate only ever runs on the plan branch, where the file never exists. Rejects: gating the plan branch
  alone, which delivers a tree no gate saw.
- `AFailedExitGateAfterTheTrialMerge_LeavesTheUsersBranchUnmoved` — the failure direction, in the
  merged-tree scenario, so the gate fails AFTER the trial merge commit exists. Assert the user's branch tip
  is still their mid-run commit. Without this row, the cheapest implementation that satisfies the rows
  above is merge-onto-the-user's-branch-then-hard-reset-on-red, a destructive write to the operator's
  checkout.
- `AFailedTrialGate_LeavesThePlanBranchUnmoved` — the same failure on the OTHER branch, in the merged-tree
  scenario. Assert the user's mid-run commit is NOT an ancestor of the plan branch (`git merge-base
  --is-ancestor <user-commit> guardrails/<plan>` exits 1). This rejects a delivery that merges the user's
  tip into the integration worktree before gating, which on red leaves commits no task authored on the plan
  branch, with no record.
- `TheTrialRefIsDeleted_AfterEitherOutcome` — the same delivering wave with a user commit landing mid-run,
  run once with its exit gate passing and once in the merged-tree scenario, failing on the trial. After
  EACH run, `refs/guardrails/trial/<waveDir>` does not exist (`git show-ref --verify` fails). Assert only
  the ref's absence here; the rows above pin what the delivery itself does.
- `APlanMarkingNoWave_StillMergesOnceAtRunEnd` — the never-weaker requirement, asserted end to end.
- `ABarrierDelivery_WithMergeOnSuccessOff_NeverPromotes` — a barrier delivery obeys the same switches as
  the run-end one (review 2026-09-13). Two green waves, the first with `delivers: true` and a passing exit
  gate, run with delivery off: `"mergeOnSuccess": false` in `guardrails.json`, as `MergeOnSuccessTests`
  does, or `--no-merge-on-success` on the in-process command. Assert the user's branch tip is exactly where
  the run started. Rejects: a barrier delivery that ignores the operator's opt-out, which moves the user's
  branch mid-run while the end-of-run banner still says nothing was delivered.
- `TheOperatorOverride_LiftsABarrierSuppression` — the `--merge-on-success` override lifts a barrier delivery
  that the §1a interlock held (a suppressing decision in a wave the delivery carries), exactly as it lifts the
  run-end one (review 2026-09-13). It never lifts the hold that follows a hook-rejected trial. A wave the delivery carries (the
  delivering wave itself or an earlier one) records a real `proceeded-unreviewed` decision: build it the
  way `RunOutcomeWiringTests` builds its proceed-unreviewed plan (autonomy policy `auto`, the
  `review-gate: "proceed-unreviewed"` threshold, and a fake `breakdown` runner). The delivering wave has
  `delivers: true` and a passing exit gate, and a LATER wave's exit gate always fails, so the run halts
  before any run-end delivery can happen. Run with `--merge-on-success`. Assert the user's branch carries
  the delivering wave's commits: only a barrier delivery can put them there. Rejects: an override honored
  at run end only.
- `ADeliversWaveWithNoExitGate_DoesNotDeliverAtItsBarrier` — a wave that sets `delivers: true` but has no
  `guardrails/` exit gate is not a delivery point (review 2026-09-13; `GR2079`, the validate warning for a
  delivering wave with no exit gate, names it). The first wave sets `delivers: true` and has no
  `guardrails/` folder; a later wave's exit gate always fails, so the run halts before run end. Assert the
  user's branch tip is exactly where the run started. Rejects: delivering behind zero checks because a wave
  with no checks "passed" its empty gate.
- `ADivergedTaskDefinition_BlocksTheBarrierDelivery` — #556 (review 2026-09-13): a task whose definition moved
  after the run loaded it still settles `succeeded`, but the run records a `definition-divergence` decision
  and delivers nothing.
  - Build the divergence the way `DivergenceDeliveryGateTests` does: in the delivering wave, one task's
    action overwrites the `task.json` of a task that depends on it, so that task's definition moves while
    the run is in flight.
  - The delivering wave has `delivers: true` and a passing exit gate, and a later wave's exit gate always
    fails, so the run halts before run end.
  - Assert the run's `decisions[]` holds a `definition-divergence` entry naming the edited task (so the
    fixture really diverged), and the user's branch tip is exactly where the run started.

  Rejects: a barrier delivery that checks only that the wave's tasks drained green, which ships work whose
  definition changed under it.
- `TheFinalWave_DeliversAtRunEnd_NotAtItsBarrier` — review round 5 (`d39-barrier-terminal-gate`): the plan's
  final wave never delivers at its barrier; the run-end delivery lands it, after the plan-level terminal gate.
  - Two waves: the first does not deliver. The FINAL wave has `delivers: true`, a passing exit gate, and a task
    that writes a file no other task writes (for example `src/final.txt`).
  - Give the plan a plan-level `guardrails/` check. It passes, and it appends one line to a log file outside
    the repo saying whether the user's branch already contains that file (`git -C <user repo> cat-file -e
    <branch>:src/final.txt`). The CLI runs this terminal gate after the Scheduler returns, so drive this row
    through the real `run` command in process.
  - Assert the log records the file ABSENT, and that after the run the user's branch carries it.

  Rejects: a barrier delivery at the final wave, which lands work before the plan-level gate has checked it
  (#457).
- `AFailedTrialTreeGate_HaltsAsAnExitGateFailure_NamingTheTrialMerge` — review round 5
  (`d39-trial-gate-failure`), in the merged-tree scenario. Assert:
  - the run halts at the delivering wave as an exit-gate failure: `RunReport.WaveHalt.Kind` is `ExitGateFailed`
    for that wave, or, when you run the real command, run.json's `halt.kind` is `wave-exit-gate-failed`;
  - the halt headline contains the user's mid-run commit's sha cut to 10 characters, and a range
    `<sha10>..<userTipSha10>` whose left side resolves to a commit on the plan branch that does not contain the
    user's mid-run commit;
  - the DURABLE headline carries the same disclosure: run.json's `halt.headline` equals the report's
    `WaveHalt.Headline`, and itself contains that sha and that range. Drive the real `run` command, or read
    run.json after constructing the Scheduler directly, the way task 14 pins the refresh disclosure;
  - the user's branch tip is still their mid-run commit;
  - the plan branch has no `Guardrails-Wave: <waveDir>` marker commit for that wave.

  Rejects: a trial-tree gate failure that halts as a refused delivery or not at all; a headline that leaves the
  operator hunting for which of their commits broke the gate; and a disclosure appended after `RecordGateHalt`,
  which reaches the console but leaves run.json and the log-site banner blaming a wave whose own tree passed.
- `AHookRejectedTrial_HoldsEveryLaterBarrierDelivery` — review round 5 (`d39-hooks-untracked-tooling`): a hook
  that fails only in a harness worktree holds deliveries instead of halting.
  - **The hook.** Install a `pre-commit` hook in the repo's `.git/hooks`. It appends the directory it runs in to a
    log file outside the repo, then exits non-zero unless an UNTRACKED file (for example `tooling.ok`, excluded
    through `.git/info/exclude`) exists in that directory. Create the file in the user's checkout only, so the
    hook passes there and fails in any harness worktree.
  - **The plan.** Three waves: the first two have `delivers: true` and passing exit gates, and the final wave
    passes. A commit lands on the user's branch mid-run, before the first wave's barrier (commit it with
    `--no-verify`), so every trial needs a merge commit.
  - **The observation.** A task or exit-gate check in the final wave appends to a second log whether the user's
    branch contains the first two waves' work at that moment.

  Assert:
  - the run did not halt: the final wave ran;
  - the hook's log records exactly ONE run outside the user's checkout: the first barrier's trial, with the
    second barrier building none;
  - the second log records the first two waves' work ABSENT from the user's branch;
  - after the run, the user's branch carries every wave's work through a merge commit whose first parent is the
    user's mid-run commit: the run-end delivery, whose hook ran in the user's checkout.

  Rejects: halting on the rejection, which throws away a run whose hook passes in the user's own checkout; and
  building a trial at every later barrier, which runs a hook already known to fail there.

**Nine of these are green on today's code, by design, and the census exempts them from the red
requirement (not from existing):**
- `AWaveWhoseExitGateFails_DoesNotDeliver`
- `AFailedExitGateAfterTheTrialMerge_LeavesTheUsersBranchUnmoved`
- `AFailedTrialGate_LeavesThePlanBranchUnmoved`
- `TheTrialRefIsDeleted_AfterEitherOutcome`
- `APlanMarkingNoWave_StillMergesOnceAtRunEnd`
- `ABarrierDelivery_WithMergeOnSuccessOff_NeverPromotes`
- `ADeliversWaveWithNoExitGate_DoesNotDeliverAtItsBarrier`
- `ADivergedTaskDefinition_BlocksTheBarrierDelivery`
- `TheFinalWave_DeliversAtRunEnd_NotAtItsBarrier`

They are green today because nothing delivers at a wave barrier:
- a failed gate already delivers nothing, moves no branch and leaves no trial ref;
- a plan marking no wave already merges once at run end;
- a run with delivery off never moves the user's branch;
- a run that halts at a wave gate never reaches the run-end delivery;
- a run with a recorded divergence never delivers at run end;
- a plan with a plan-level `guardrails/` folder already delivers once, after that gate passes.

Write them honestly — do NOT couple them to the missing feature to force a red. Task 08's forward census
requires them Passed once delivery lands. That is where any of these turns them red: a merge-before-gate, a
leaked trial ref, an ignored opt-out, a delivery behind an empty gate, a delivery past a divergence, or a
barrier delivery at the final wave.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

The tests MUST COMPILE, and the other six MUST FAIL. Do NOT wire the delivery.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/WaveDelivery/WaveBarrierDeliveryTests.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
