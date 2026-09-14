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

**PENDING (round 5, `d39-barrier-terminal-gate`): which waves stay with the run-end call.** Before review this
read: the run-end call stays for every flat plan and for the waves after the last delivery point. Round 5
decides the plan's final wave, and plans with a plan-level `guardrails/` folder (#457).

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

The two call sites differ only in the suppressing decision they pass in. `Finalize` passes the run-scoped
`RunOutcomePolicy.SuppressingDecision(decisions)`, exactly as today. The barrier passes
`RunOutcomePolicy.SuppressingDecisionForDelivery(decisions, coveredWaves)` (task 06) over the SET of waves this
delivery carries: every wave since the previous delivery point, this one included (review round 4,
`d39-interlock-ride-along`). Task 29 later derives that set from the journal so it survives a resume.

**PENDING (round 5, `d39-hooks-untracked-tooling`).** If a hook-rejected trial is to hold deliveries instead of
halting, the shared decision gains one more barrier term: no earlier hook-rejected refusal in this run.

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
3. **Gate the trial tree.** Check these in this order:
   - **`trial.AlreadyDelivered` FIRST.** The trial's tree is already on the user's branch (a resume after a
     crash that followed the promotion), so run no gate and skip the promotion. An already-delivered trial has
     no `WorktreePath`, and one built from equal tips also has `UserTipWasAncestor` true, so checking either of
     the next two first gates in a worktree that does not exist, or promotes again.
   - Otherwise, when `trial.UserTipWasAncestor` is true, the trial IS the plan-branch tip, so the wave's exit
     gate that already ran on the integration worktree is that gate. Do not run it twice.
   - Otherwise run the SAME exit gate again in `trial.WorktreePath`, a harness-owned worktree checked out at
     `trial.Commit`. Give the existing exit-gate evaluation its workspace as a parameter rather than
     duplicating it; grep `RunWaveExitGateAsync`, which reads `integ.IntegrationWorktreePath` today.
4. **Promote only on green.** `PromoteTrialDelivery(integ, trial, ct)` re-checks #588 and #448 and
   fast-forwards. Never call it for an already-delivered trial or after a failed trial-tree gate.
5. **Always discard.** Call `DiscardTrialDelivery(integ, waveDir)` in a `finally`, so a thrown gate leaves no
   trial ref and no trial worktree behind.

**PENDING (round 5, `d39-trial-gate-failure`): how a failed trial-tree gate halts.** Until that is decided, the
requirement is what task 07 pins: no promotion, neither branch moves, and the trial is discarded. Whichever way
it halts, the report of a failed trial-tree gate names three things (lead decision, 2026-09-13):
- each failing check;
- `trial.UserTip`;
- the range `<planTipSha10>..<userTipSha10>`, over which `git log` lists exactly the user's commits the trial
  merged. `<planTipSha10>` is the plan-branch tip the trial was built from (`CurrentPlanBranchTip(integ)` at the
  barrier) and `<userTipSha10>` is `trial.UserTip`, each cut to 10 characters. Key the range on shas, never on
  a branch name, which moves.

Do not add a commit-list member to `TrialDelivery` for this.

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
- the #556 divergence (`ADivergedTaskDefinition_BlocksTheBarrierDelivery`).

This task's forward census requires all twelve of task 07's rows to pass.

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
