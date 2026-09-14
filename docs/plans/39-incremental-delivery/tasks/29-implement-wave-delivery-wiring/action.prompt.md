## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `29-implement-wave-delivery-wiring`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "29-implement-wave-delivery-wiring": { "someKey": "someValue" } }`.
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

Make `WaveDeliveryWiringTests` pass. Design 39 §4 ("The record, pinned at review") and §5 ("Wiring and
halts"): the Scheduler writes `waves.<dir>.delivered` around every barrier delivery, raises
`IRunObserver.WaveDelivered` only after a `delivered` record is persisted, and `BuildReport` stamps
`RunReport.WaveDeliveries` from the journal. The first breakdown built the record and the event and wired
neither, which is #120's shape. This task is the wiring.

**Where, and what is already decided there.** Task 08 owns the barrier delivery DECISION and its order. At a
wave whose `WaveNode.IsDeliveryPoint` is true, it:

1. applies the one delivery predicate it shares with `Scheduler.Finalize`: `plan.Config.MergeOnSuccess`, the
   #361 interlock over the waves the delivery carries, lifted by `plan.Config.MergeOnSuccessForcedByOperator`,
   the serial guard (a worktree provider and an integration handle both present), #556's
   executed-definition divergence, which `Finalize` honors through `RunReport.AllSucceeded`, and, at a
   barrier, no hook-rejected trial earlier in this run (round 5, `d39-hooks-untracked-tooling`);
2. calls `CreateTrialDelivery`;
3. gates the trial;
4. calls `PromoteTrialDelivery`, unless the trial reports `AlreadyDelivered`;
5. calls `DiscardTrialDelivery` in a `finally`.

A barrier never delivers at the plan's final wave (round 5, `d39-barrier-terminal-gate`): that wave delivers
at run end through `Finalize` and gets no barrier record.

This task adds the record, the event and the report around that flow. It never adds a second predicate, a
second interlock call, or a second trial. If `ASerialWavedRun_NeverDeliversAtABarrier`,
`ABarrierDelivery_WithMergeOnSuccessOff_WritesNoRecord` or `ADivergedTaskDefinition_BlocksTheBarrierDelivery`
fails, the cause is either a write you placed before
task 08's predicate or that shared predicate itself: fix the predicate in place, never duplicate it. Grep
`Scheduler.cs` for `CreateTrialDelivery`, `PromoteTrialDelivery`, `BuildReport` and `RecordWaveCompleted`
rather than trusting a line number: this file has moved under several plans. Never call
`MergePlanBranchIntoUserBranch` at a barrier; the run-end delivery keeps it.

**Reach the journal the way the supply drain does.** `RecordWaveDelivery` is on `RunJournal`, not on
`ISchedulerJournal`, and `ISchedulerJournal.cs` is outside this scope. Use the
`if (_journal is Journal.RunJournal runJournal)` pattern the Scheduler already uses before
`RecordSupplied`. A unit-test fake journal models no durable document, and records nothing.

**1. `covers`, from the journal.** Walk `plan.Waves` back from this wave, reading each earlier wave through
`_journal.WaveEntryOf(dir)`, and stop at the nearest one whose `Delivered` record has status `Delivered`.
`covers` is every wave after that one, in plan order, ending with this wave. Never compute it from state held
in memory: a resume discards that state, and `CoversAfterAResume_StillStartsAfterTheLastDeliveredWave` is the
row that catches it. An earlier delivery that was refused or suppressed does not end the walk: its work is
still not on the user's branch, so this delivery carries it.

Hand this set to the interlock call task 08 already makes,
`RunOutcomePolicy.SuppressingDecisionForDelivery(decisions, coveredWaves)`, replacing whatever set task 08
computed in memory (its prompt defers the journal-derived set to this task). Do not add a second call. A held
wave's machine-decided work riding along holds the delivery (review round 4), and a suppressing decision with
no `Wave` holds every delivery: task 06 fails closed.

**2. The writes.** Every record carries `StartedAt`, taken when the barrier delivery begins, and `Covers`.
Leave every field you do not set null; the record's serialization omits null fields (tasks 09/10), so never
write a placeholder. Place every write after task 08's predicate.

- **No delivery at this barrier:** write nothing when delivery resolved off (`mergeOnSuccess` is false, or
  the run is serial), when #556 withholds it (a task definition was edited mid-run), or at the plan's final
  wave, which delivers at run end. A missing key does
  not by itself say where the work is. Design §4's rule: a wave with no key has its work on the user's branch
  only if a later barrier delivery's `covers` includes it or the run-end delivery landed, and a serial run
  has no plan branch at all.
- **The interlock holds it** (a suppressing decision the operator did not override): write `suppressed`,
  already settled, with `At` and a `Detail` naming the decision's token and its subject. No trial is built,
  so nothing the operator can see happens first.
- **An earlier barrier's trial was hook-rejected** (round 5, `d39-hooks-untracked-tooling`: task 08's
  predicate then holds this and every later barrier delivery to run end, where the merge runs the user's hooks
  in their own checkout): write `suppressed`, already settled, with `At` and a `Detail` naming that earlier
  wave's directory and `hook-rejected`. No trial is built.

  **The journal drives the HOLD, not only its detail.** Any wave before this one in plan order whose
  `delivered` record reads `refused` with outcome `hook-rejected` holds this delivery. Task 08 could only
  track the rejection in process memory, because no delivery records existed on its base, and a resume
  forgets that flag. Replace 08's in-memory flag with this journal lookup, or feed the flag from it, fixing
  the shared predicate in place; never add a second hold. Use the same lookup to name the rejecting wave in
  the `Detail`. `AResumeAfterAHookRejection_StillHoldsLaterDeliveries` is the row that catches a hold a
  resume forgets.
- **Otherwise, the delivery begins.** First capture the wave's prior record if it reads `delivered` (a resume
  after a crash that followed the settled write, or a rewound wave running again). Then write `running`
  (no `At`) BEFORE `CreateTrialDelivery`, always, even over that prior record. That is #625: journal the
  state before the first action the operator can see, and the trial merge runs the user's git hooks. Then
  settle by REPLACING the `running` record:
  - `trial.Refusal` is set → `refused`, with `At`, the matching `Outcome`, and `Detail` from
    `trial.RefusalDetail`. Nothing is gated or promoted. A `HookRejected` refusal does not halt the run:
    task 08's predicate holds every later barrier delivery to run end instead (round 5).
  - The trial-tree gate failed (the trial needed a merge commit, and task 08's gate over
    `trial.WorktreePath` did not pass) → `refused`, with `At`, `Outcome` `TrialGateFailed`, and a `Detail`
    naming each failing check, the user tip the trial merged (`trial.UserTip`), and the range
    `git log <planTipSha10>..<userTipSha10>`, which lists exactly the user's commits the trial merged. Take
    the plan tip from `CurrentPlanBranchTip(integ)` at the barrier, and cut both shas to their first 10
    characters. Never put a branch name in that range: it goes stale once the plan branch advances. Nothing
    is promoted. Task 08 then halts through the exit-gate halt on the trial tree (round 5,
    `d39-trial-gate-failure`).
  - `trial.AlreadyDelivered` WITH a captured prior `delivered` record (a pure resume: the delivery landed
    and was recorded before the crash) → restore that prior record verbatim, the same `At` and `Commit`, and
    raise no event. Task 08 skips the promotion.
  - `trial.AlreadyDelivered` WITHOUT a prior `delivered` record (a crash between the fast-forward and the
    settled write) → a fresh `delivered`, with `At`, `Outcome` `FastForwarded`, and `Commit` = the trial's
    `Commit`, announced once. Task 08 skips the promotion.

    Never settle an `AlreadyDelivered` trial as `refused`: the trial, not the status of an earlier record,
    is what tells a resume that the delivery already landed.
  - `PromoteTrialDelivery` returned `FastForwarded` → `delivered`, with `At`, `Outcome` `FastForwarded`, and
    `Commit` = the trial's `Commit` (after a fast-forward the user's branch tip IS that commit; task 30 pins
    it). When the operator override lifted a suppressing decision for this delivery, `Detail` names that
    decision's token and its subject, so `run.json` keeps #597's trace of the override.
  - Any other promotion result → `refused`, with `At`, the matching `Outcome`, and `Detail` from the
    provider's `LastMergeOnSuccessDetail`.

  Every other trial proceeds normally, and its settled record replaces whatever the wave had before. That is
  how a rewound wave's re-run records a new delivery.
- **A wave whose exit gate fails before its barrier writes no record.** Its delivery never began.

Map a `MergeOnSuccessResult` to the `DeliveryOutcome` member of the same name; both enums already exist.
`DeliveryOutcome.TrialGateFailed` has no `MergeOnSuccessResult` counterpart; task 09 declares it.

**3. The event.** Raise `_observer.WaveDelivered(wave, record)` only for a `delivered` record, only after
`RecordWaveDelivery` has returned, and pass the SAME instance you wrote. Never raise it for `running`,
`refused` or `suppressed`, or for a prior record you restored: that delivery was already announced.

**4. What follows the settled write.** Keep `CommitWaveMarker` and `RecordWaveCompleted` AFTER the settled
write, and leave the code between them open. Task 17 adds the `WaveHaltKind.DeliveryRefused` halt right
after the settled write for a refusal that halts, so a resume re-attempts it at that wave's barrier; a
`hook-rejected` trial holds later deliveries instead (round 5), and task 08 halts a failed trial-tree gate. Task 15 adds the
post-delivery refresh and its `RecordRefreshed` before the marker, so a crash cannot skip the refresh. This
task does neither.

**5. `BuildReport` stamps `WaveDeliveries` on every report.** `BuildReport` is the one method every report
passes through, halted or not. Fill `WaveDeliveries` there from `plan.Waves`: every wave whose
`_journal.WaveEntryOf(dir)?.Delivered` is set, keyed by the wave's directory. A wave without a record is
absent. Never stamp it only in `Finalize`: a wave gate or barrier halt returns before `Finalize`, and
`AHaltedRunsReport_StillCarriesEarlierWaveDeliveries` pins that path. Task 19 derives the
`partially-delivered` outcome from this map. In `RunReport.cs`, document the property; its default stays
empty.

**Not this task.** The delivery decision and the trial's order are task 08's. The refused-delivery halt and
its `decisions[]` entry are task 17's, the `partially-delivered` outcome task 19's, the post-delivery refresh
task 15's, and forwarding `WaveDelivered` through the observer decorators tasks 12/13's.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs` and `src/Guardrails.Core/Execution/RunReport.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
