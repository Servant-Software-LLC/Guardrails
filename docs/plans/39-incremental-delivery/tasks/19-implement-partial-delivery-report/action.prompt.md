## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `19-implement-partial-delivery-report`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "19-implement-partial-delivery-report": { "someKey": "someValue" } }`.
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

Make `PartialDeliveryReportTests` pass. Design 39 §4 (round 4, `d39-partial-delivery-record`).

Order is load-bearing: print the delivery block **before** the verdict. The #340 banner printed after
the summary and was read straight past by a real operator.

**run.json's delivery record.** In `RunCommand.DescribeDelivery`, derive the outcome from
`RunReport.WaveDeliveries`, which task 29 stamps in `BuildReport` on every report, halted or not:

- When at least one wave's record reads `delivered` and verified work is still held on the plan branch,
  return `DeliveryOutcome.PartiallyDelivered` with `Delivered = false`. `delivered` stays true only when
  ALL verified work reached the user's branch.
- **Precedence: partially-delivered wins whenever work both landed and is still held.** Decide it before
  `WhollyGreenButUndelivered` and every other never-attempted reason, and before returning a refused
  run-end merge's own outcome. A run with no `delivered` wave record reads exactly as it does today.
- **What "held" means.** A wave with no `delivered` key is NOT held when the run-end merge landed
  (`FastForwarded` or `Merged`): that merge carried every wave after the last delivery point, so the run
  is delivered, exactly as a flat plan's is. Work is held only when the run-end delivery did not land —
  it halted first, was suppressed, was refused, or delivery resolved off. The same holds for a wave whose
  barrier record reads `refused` with `hook-rejected`, or `suppressed` because that rejection held it
  (review round 5, `d39-hooks-untracked-tooling`): a rejecting hook holds deliveries to run end instead of
  halting, and a run-end merge that landed carried those waves too, so the run is delivered.
  `DescribeDelivery_AHookRejectionHeldDeliveriesThenTheRunEndMergeLanded_IsDelivered` pins it.
- **Every `DeliveryOutcome` member is a possible wave-record outcome**, including `TrialGateFailed` (a
  failed trial-tree gate, added by task 09). Any switch you write over `DeliveryOutcome` handles it without
  throwing, and names outcomes through `JournalJson.DeliveryOutcomeToken`.
  `DescribeDelivery_APartialDelivery_IsPartiallyDelivered` builds its held wave from exactly that record.
- Set both `DeliveredToBranch` and `PlanBranch`.
- Make `Reason` name the delivered waves and the held waves. When the interlock held the run-end
  delivery, `Reason` still names the suppressing decision, its subject and its boundary, as today's
  sentence does. When the run-end merge ran and was refused, `Reason` names the refusal's token, and
  `Detail` keeps `MergeOnSuccessDetail`.
- Add the `partially-delivered` token to `JournalJson.DeliveryOutcomeToken` AND to
  `DeliveryOutcomeConverter.Read`. The converter throws on an unknown member, and a record that writes
  but cannot be read back breaks the next resume (#625) — the resume that re-attempts the held wave.
  `PartiallyDelivered_RoundTripsThroughTheJournal` pins both directions.

**Record the delivery before the terminal-gate early return (review round 5, `d39-barrier-terminal-gate`).**
The plan's final wave always delivers at run end, after the plan-level terminal gate, while earlier waves
deliver at their barriers. When that gate fails, `RunCommand` returns early (grep for
`PrintTerminalGateFailure`; `RunCommand.cs:829` today) before `journal.RecordDelivery` (`:847`), so a run
whose earlier waves already delivered writes no delivery record at all. Move the record ahead of that return,
keeping its best-effort handling and the task-failed exit code, so it reads `partially-delivered` with a
`Reason` naming the failed terminal gate. `ATerminalGateFailureAfterAWaveDelivered_StillRecordsPartiallyDelivered`
pins the outcome, and `ATerminalGateFailureAfterAWaveDelivered_WritesTheDeliveryRecordBeforeReturning` pins
the order.

**The banner.** `RenderUndeliveredWorkWarning` names the waves that already delivered and says they are
on the user's branch. Its "NOT on your checkout" wording stays true only for the work still held.

**The console label.** `PrintWaveHalt` (public since task 18) prints `WaveHaltKind.DeliveryRefused` under
its own label, `WAVE DELIVERY REFUSED`. Today it falls through to the generic `WAVE HALT`, and design 39 §4
gives a refusal its own halt kind precisely so the operator reads a refusal, not a failed gate or an
unexplained halt.

**The existing delivery contracts stay green.** This task's `01-tests-pass.ps1` also runs
`DeliveryRecordTests` and `UndeliveredWorkWarningTests`, so a precedence change that breaks a flat plan's
record or banner halts here rather than at the plan's terminal gate.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Commands/RunCommand.cs`,
`src/Guardrails.Core/Journal/JournalModel.cs`, and `src/Guardrails.Core/Journal/JournalJson.cs`. After
this task completes, the harness runs a `git diff` membership check and rejects any edit outside these
paths. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error
caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
