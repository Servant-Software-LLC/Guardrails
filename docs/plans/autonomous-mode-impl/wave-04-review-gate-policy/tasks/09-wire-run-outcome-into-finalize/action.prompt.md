## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/09-wire-run-outcome-into-finalize` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

**Wire the run-outcome policy into the finalize path — PART 1 of 2** (issue #361 Phase 4; the #120 lesson;
#340 delivery reconciliation). Task 03 built the pure `RunOutcomePolicy`; your slice consults it at the
end-of-run Finalize so machine-decided work is not auto-delivered and the unreviewed-wave count is
surfaced. Task 10 then adds the distinct exit code and drives the full integration proof.

**Architecture caveat (#203) — verify before you build on it.** Cite DURABLE markers (grep the symbols),
never line numbers. Confirm each still holds in the materialized tree (which now includes task 05's
Scheduler.cs review-gate resolution):
- `Scheduler` Finalize — grep the shipped `mergeOnSuccess` delivery gate:
  `report.AllSucceeded && plan.Config.MergeOnSuccess && _worktreeProvider != null && integ != null` and
  the `RunReport.WhollyGreenButUndelivered` assignment. TODAY that gate consults ONLY
  `plan.Config.MergeOnSuccess` — it does NOT read the run's recorded decisions.
- `RunOutcomePolicy` (`src/Guardrails.Core/Execution/RunOutcomePolicy.cs`, task 03) —
  `SuppressesDelivery(decisions)` and `ProceededUnreviewedWaveCount(decisions)`.
- The run's recorded decisions — grep how Finalize reaches the journal's `decisions[]` (the shipped
  `RunJournal` / `DecisionEntry` list the classify-then-act + review-gate paths append to).
- `RunReport` (`src/Guardrails.Core/Execution/RunReport.cs`) — mirror the existing `WhollyGreenButUndelivered`
  field style when you add the unreviewed-wave count surface.

Wire it (design of record doc 12 §1 hard rule + §5.2 Option P):
- **mergeOnSuccess defaults OFF on a machine decision.** In Finalize, when
  `RunOutcomePolicy.SuppressesDelivery(<the run's recorded decisions>)` is true, resolve delivery OFF (do
  NOT merge the plan branch into the user's branch) EVEN when `plan.Config.MergeOnSuccess` is true —
  UNLESS the operator forced it with an explicit `--merge-on-success` (respect an explicit override;
  grep the shipped `MergeOnSuccessExplicit` / the CLI override precedence). Set
  `RunReport.WhollyGreenButUndelivered` so the shipped green-but-undelivered warning fires.
- **Surface the unreviewed-wave count.** Add a `RunReport` surface (mirroring the existing field style)
  carrying `RunOutcomePolicy.ProceededUnreviewedWaveCount(<decisions>)` so a run can be permanently flagged
  "ran with N unreviewed waves" (the CLI rendering + the distinct exit code are task 10).
- Do NOT change `RunOutcomePolicy` (task 03 owns it) or the review-gate resolution (task 05). Do NOT add
  the exit code here (task 10). Do NOT write a review marker anywhere.

**In-attempt regression check (issue #253 + #374 — do NOT skip, and run it PLAINLY):** run ONLY the
targeted filter, via the **Bash tool**, as a **plain** command — no `&`, no pipe, no `2>&1 |`, not the
PowerShell tool (issue #374): `dotnet build Guardrails.sln -c Debug` (confirm the whole solution compiles)
and `dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~RunOutcomePolicyTests"` (confirm
you did not regress the policy). Do NOT run the full unfiltered `dotnet test
tests/Guardrails.Integration.Tests` (fixture leak, #253).

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs` and
`src/Guardrails.Core/Execution/RunReport.cs`. The harness runs a post-action `git diff` membership check
and REJECTS any edit outside these two paths — including `RunOutcomePolicy.cs` (task 03), the authored
tests, or `ExitCodes.cs`/`RunCommand.cs` (task 10). An out-of-scope edit fails the task immediately and
consumes a retry. If the finalize wire genuinely needs a change to another file (e.g. no decisions accessor
is reachable from Finalize), do NOT edit it — write `{"needsHuman": "<what is missing>"}` and stop.

Completion criteria (your guardrails check these): the solution builds; `Scheduler.cs` Finalize consults
`RunOutcomePolicy` (SuppressesDelivery) to resolve delivery; and `RunReport.cs` surfaces the
unreviewed-wave count.
