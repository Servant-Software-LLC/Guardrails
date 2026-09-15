## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `08-implement-deliver-at-wave-barrier`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-implement-deliver-at-wave-barrier": { "someKey": "someValue" } }`.
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

Make `WaveBarrierDeliveryTests` pass. Design 39 §1/§3: a wave that is a delivery point delivers at its OWN barrier,
through the trial merge, gated on that wave's `Exit` being green.

**The plan's final wave always delivers at run end (review round 5, `d39-barrier-terminal-gate`).** Never
barrier-deliver at the plan's FINAL wave, whatever its `IsDeliveryPoint` says and whether or not the plan has a
plan-level `guardrails/` folder. Never key this on `plan.PlanGuardrails`: the rule is about the wave's position. Leave it, together with every
wave after the last earlier delivery point, to `Finalize`'s run-end delivery, which already waits for a
plan-level `guardrails/` terminal gate to pass (#457). Earlier waves that are delivery points still deliver at
their own barrier, even in a plan with a plan-level `guardrails/` folder: that gate checks the whole plan and
cannot run until every wave has finished. Every flat plan keeps the run-end call.

**One delivery decision, shared with run end (review 2026-09-13).** `Scheduler.Finalize` already decides whether
the run-end delivery may happen. EXTRACT that decision into one method both call sites use — never a copy that
can drift — and carry EVERY term that can withhold delivery in `Finalize` today. Grep `Finalize` and
`RunReport.AllSucceeded` for them rather than trusting this list, which is what they read at the time of
writing:

- `plan.Config.MergeOnSuccess` (#340);
- the #361 interlock over `decisions[]`, lifted by `plan.Config.MergeOnSuccessForcedByOperator`, the
  `--merge-on-success` flag (#597);
- the serial guard: `_worktreeProvider != null && integ != null`;
- every conjunct of `RunReport.AllSucceeded`: no definition drift, no wave halt, not aborted, every task green,
  and **no executed-definition divergence** (`HasExecutedDefinitionDivergence`, #556).

The divergence term is the easy one to miss. `CheckExecutedDefinitionDivergence` lets a task whose definition
moved while it ran settle `succeeded`, and records the divergence in a list only `BuildReport` reads, so a wave's
tasks drain green and its exit gate passes over work #556 says must never ship. At a barrier, read each term over
the run SO FAR, and fail closed: any divergence recorded so far in this run withholds this barrier's delivery
and every later one.

**Never call `BuildReport(...).AllSucceeded` at a barrier.** `BuildReport` marks every task that has not started
yet `Cancelled`, and `IsGreen` accepts only `Succeeded` or `Skipped`, so that call is false at every barrier
before the last and nothing would ever deliver. At a barrier, "every task green" means every task in the waves
drained so far, and "no divergence" means none recorded so far. The extracted method takes those terms as
inputs: the barrier passes its per-barrier values, and `Finalize` passes the run-end ones it reads from the
finished report today.

The two call sites differ only in the suppressing decision they pass in. `Finalize` passes the run-scoped
`RunOutcomePolicy.SuppressingDecision(decisions)`, exactly as today. The barrier passes
`RunOutcomePolicy.SuppressingDecisionForDelivery(decisions, coveredWaves)` (task 06) over the SET of waves this
delivery carries: every wave since the previous delivery point, this one included (review round 4,
`d39-interlock-ride-along`). Task 29 later derives that set from the journal so it survives a resume.

**A hook-rejected trial holds deliveries; it does not halt (review round 5, `d39-hooks-untracked-tooling`).** The
trial merge commit runs the user's hooks in a harness worktree. There, a hook that needs untracked tooling from
the user's own checkout (a gitignored `node_modules`, say) fails, even though the same hook passes in that
checkout. So the shared decision gains one more barrier term: **no hook-rejected trial earlier in this run**.
- Once a trial comes back `HookRejected`, every later barrier delivery is held and builds no trial.
  `--merge-on-success` does not lift this hold: the override lifts only a delivery the §1a interlock held.
- Track the rejection in the Scheduler for the run. Task 29 records the rejecting wave's delivery as `refused`
  and each later held one as `suppressed`.
- At run end, `Finalize` delivers normally, and its merge runs the hooks in the user's own checkout.

`Finalize`'s observable behavior must not change: its #457 terminal-gate deferral, its #597 "forced past a
decision" flag and its #340 `WhollyGreenButUndelivered` flag stay exactly as they are. `MergeOnSuccessTests`,
`RunOutcomeWiringTests` and `DivergenceDeliveryGateTests` pin that behavior, and this task's tests-pass
guardrail runs all three.

**At a wave that is a delivery point.** Read `WaveNode.IsDeliveryPoint` (task 02: `delivers: true` AND at least
one exit-gate check), never `WaveNode.Delivers` alone, so a wave with no checks never delivers behind an empty
gate. **NEVER call `MergePlanBranchIntoUserBranch` at a barrier**; use the trial-delivery primitive (task 31,
review round 4 `d39-trial-delivery-primitive`):

1. **Decide first.** Evaluate the shared decision. If any term withholds delivery, deliver nothing at this
   barrier and build no trial: `CreateTrialDelivery` runs the user's git hooks, and a delivery the decision
   withholds must never run them.
2. **Build the trial.** `CreateTrialDelivery(integ, waveDir, ct)` builds `refs/guardrails/trial/<waveDir>`. A
   trial whose `Refusal` is set could not be built: do not gate it or promote it.
   - A `HookRejected` refusal does NOT halt the run. Discard the trial, remember the rejection for the shared
     decision above, and carry on with the next wave.
   - A `Conflict` refusal is the halt tasks 16/17 build.
3. **Gate the trial tree.** Check these in this order:
   - **`trial.AlreadyDelivered` FIRST.** The trial's tree is already on the user's branch (a resume after a
     crash that followed the promotion), so run no gate and skip the promotion. An already-delivered trial has
     no `WorktreePath`, and one built from equal tips also has `UserTipWasAncestor` true, so checking either of
     the next two first gates in a worktree that does not exist, or promotes again.
   - Otherwise, when `trial.UserTipWasAncestor` is true, the trial IS the plan-branch tip, so the wave's exit
     gate that already ran on the integration worktree is that gate. Do not run it twice.
   - Otherwise run the SAME exit gate again in `trial.WorktreePath`, a harness-owned worktree checked out at
     `trial.Commit`. Give the existing exit-gate evaluation its workspace as a parameter rather than
     duplicating it; grep `RunWaveExitGateAsync`, which reads `integ.IntegrationWorktreePath` today. A failure
     there halts the run as an exit-gate failure, described below.
4. **Promote only on green.** `PromoteTrialDelivery(integ, trial, ct)` re-checks #588 and #448 and
   fast-forwards. Never call it for an already-delivered trial or after a failed trial-tree gate.
5. **Always discard.** Call `DiscardTrialDelivery(integ, waveDir)` in a `finally`, so a thrown gate leaves no
   trial ref and no trial worktree behind.

**A failed trial-tree gate is an exit-gate failure (review round 5, `d39-trial-gate-failure`).** A gate really
failed, on the tree the delivery would produce. So halt through the EXISTING exit-gate path, exactly as a failed
exit gate on the plan branch halts today (grep for where `RunWaveExitGateAsync`'s result is checked):
- the wave settles needs-human, later waves are blocked, and NO wave-completion marker is written;
- the halt is `BuildGateHalt(wave, WaveHaltKind.ExitGateFailed, <the trial gate's failed checks>)`, recorded with
  `RecordGateHalt`, so run.json gets its `halt` section and the log site shows its banner;
- nothing is promoted, and the trial is still discarded in the `finally`.

The wave's recorded exit reads failed, and the wave-keyed gate logs hold the trial run's output. Both are
intended: a gate failed on the tree that would have landed, and a resume re-runs the exit gate anyway.

After the failing check names, the halt headline says the gate failed on the trial merge with the user's
branch, and names:
- `trial.UserTip`, cut to 10 characters;
- the range `<planTipSha10>..<userTipSha10>`, over which `git log` lists exactly the user's commits the trial
  merged. `<planTipSha10>` is the plan-branch tip the trial was built from (`CurrentPlanBranchTip(integ)` at the
  barrier) and `<userTipSha10>` is `trial.UserTip`, each cut to 10 characters. Key the range on shas, never on
  a branch name, which moves.

**Compose the FULL headline, trial disclosure included, BEFORE calling `RecordGateHalt`.** `RecordGateHalt` copies
the headline into run.json's `halt.headline`, and the log-site banner reads it from there. A disclosure appended
afterward reaches the console only: run.json and the banner would then say just "exit gate FAILED" over a wave
whose own tree passed. The disclosure is what makes that halt fair to the wave, which is why round 5 accepted it.
Task 07 pins `halt.headline` equal to `WaveHalt.Headline`.

Do not add a commit-list member to `TrialDelivery` for this. Task 15 later appends its unauthored-content note to
`BuildGateHalt` headlines, after this disclosure. Task 29 records the delivery as `refused` / `trial-gate-failed`.

On red, neither branch moves: never merge the user's tip into the integration worktree before the gate passes.
Task 07 pins:

- a delivering wave merging at its own barrier (`ADeliveringWaveMergesAtItsOwnBarrier`), carrying the waves since
  the last delivery point (`ANonDeliveringWaveRidesAlongToTheNextDeliveryPoint`), and the gate on the merged tree
  (`TheGateRunsAgainstTheMergedTree_NotThePlanBranchAlone`);
- a failed gate delivering nothing (`AWaveWhoseExitGateFails_DoesNotDeliver`), and a plan marking no wave still
  merging once at run end (`APlanMarkingNoWave_StillMergesOnceAtRunEnd`);
- both failure directions (`AFailedTrialGate_LeavesThePlanBranchUnmoved`,
  `AFailedExitGateAfterTheTrialMerge_LeavesTheUsersBranchUnmoved`);
- the cleanup (`TheTrialRefIsDeleted_AfterEitherOutcome`);
- the opt-out (`ABarrierDelivery_WithMergeOnSuccessOff_NeverPromotes`);
- the override (`TheOperatorOverride_LiftsABarrierSuppression`);
- the empty gate (`ADeliversWaveWithNoExitGate_DoesNotDeliverAtItsBarrier`);
- the #556 divergence (`ADivergedTaskDefinition_BlocksTheBarrierDelivery`);
- the final wave, with and without a plan-level gate (`TheFinalWave_DeliversAtRunEnd_NotAtItsBarrier`,
  `TheFinalWave_WithNoPlanLevelGate_StillDeliversAtRunEnd`);
- the trial-tree gate halt (`AFailedTrialTreeGate_HaltsAsAnExitGateFailure_NamingTheTrialMerge`);
- the hook hold (`AHookRejectedTrial_HoldsEveryLaterBarrierDelivery`).

This task's forward census requires all sixteen of task 07's rows to pass.

**Not this task.**
- Task 29 writes `waves.<dir>.delivered`, including its `running` state before the trial is built, and raises
  `IRunObserver.WaveDelivered`.
- Tasks 16/17 halt on a refused delivery and record its `decisions[]` entry.
- Task 15 owns the post-delivery refresh.

Never treat a wave as delivered unless `PromoteTrialDelivery` returned `FastForwarded` or the trial was
`AlreadyDelivered`.

**Find the seams yourself.** Grep `Scheduler.cs` for `RunWavedAsync`, `Finalize`, `DeliverToUserBranch`,
`RunWaveExitGateAsync` and `CheckExecutedDefinitionDivergence` rather than trusting a line number — this file
has moved under several plans and a cited line is stale on arrival. `DeliverAndCleanup` never existed; the name
entered at charter review.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
