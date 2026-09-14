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

Make `WaveBarrierDeliveryTests` pass. Design 39 §1/§3: a delivering wave delivers at its OWN barrier,
gated on that wave's `Exit` being green. **The run-end call stays** for every flat plan and for the waves
after the last delivery point.

**Use the trial-delivery primitive (task 31, review round 4 `d39-trial-delivery-primitive`); NEVER call
`MergePlanBranchIntoUserBranch` at a barrier.** At a delivering wave's barrier:

1. `CreateTrialDelivery(integ, waveDir, ct)` builds `refs/guardrails/trial/<waveDir>`: the plan branch
   merged onto the user's tip, with the user's hooks run on any merge commit. A trial whose `Refusal` is
   set could not be built — do not gate it or promote it.
2. Run the wave's `Exit` gate against the TRIAL tree (`trial.Commit`), not the plan branch alone: it is the
   tree the delivery would produce.
3. Consult the interlock over the SET of waves this delivery carries — every wave since the previous
   delivery point, this one included — through `RunOutcomePolicy.SuppressingDecisionForDelivery(decisions,
   coveredWaves)` (task 06). A held wave's work riding along holds the delivery (review round 4,
   `d39-interlock-ride-along`). Task 29 later derives that set from the journal so it survives a resume.
4. Only on a green gate with no suppressing decision, `PromoteTrialDelivery(integ, trial, ct)`, which
   re-checks #588 and #448 and fast-forwards.
5. `DiscardTrialDelivery(integ, waveDir)` after EITHER outcome — in a `finally`, so a thrown gate leaves
   no ref behind.

On red, neither branch moves: never merge the user's tip into the integration worktree before the gate
passes. Task 07 pins both failure directions (`AFailedTrialGate_LeavesThePlanBranchUnmoved`,
`AFailedExitGateAfterTheTrialMerge_LeavesTheUsersBranchUnmoved`) and the cleanup
(`TheTrialRefIsDeleted_AfterEitherOutcome`), and this task's forward census requires them `Passed`.

**Not this task.** Writing `waves.<dir>.delivered` and raising `IRunObserver.WaveDelivered` belong to task
29, around this promotion. Halting on a refused promotion belongs to tasks 16/17, and the post-delivery
refresh to task 15. Never treat a wave as delivered unless `PromoteTrialDelivery` returned
`FastForwarded`.

**Find the seams yourself.** Grep `Scheduler.cs` for `RunWavedAsync`, `Finalize` and `DeliverToUserBranch`
rather than trusting a line number — this file has moved under several plans and a cited line is stale
on arrival. `DeliverAndCleanup` never existed; the name entered at charter review.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
