## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `18-author-tests-partial-delivery-report`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "18-author-tests-partial-delivery-report": { "someKey": "someValue" } }`.
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

Author failing tests for design 39 §4 — **a partially-delivered run is a NEW run outcome and must
render as neither of the two that exist**, both in the printed report and in run.json's delivery
record.

**Test file:** `tests/Guardrails.Integration.Tests/WaveDelivery/PartialDeliveryReportTests.cs`
**Test class:** `PartialDeliveryReportTests`

**Stubs, so the tests compile:**

- Add one member, `PartiallyDelivered`, to `DeliveryOutcome` in `src/Guardrails.Core/Journal/JournalModel.cs`.
  Do NOT add its token: task 19 owns the `partially-delivered` token in `JournalJson`, whose converter
  throws on an unknown member in both directions. Only `PartiallyDelivered_RoundTripsThroughTheJournal`
  writes the new member through the journal, and it is red for exactly that reason until task 19 adds
  the token to the writer AND the reader. No other test in this suite serializes it.
- In `src/Guardrails.Cli/Commands/RunCommand.cs`, make two changes and nothing else. Change `PrintWaveHalt`
  from `private static` to `public static`: it is the method that prints a wave halt's label. And add the
  end-of-run delivery report's renderer as a throwing stub,
  `public static void RenderWaveDeliveryReport(RunReport report, TextWriter output) => throw new NotImplementedException();`,
  which task 19 implements and calls before the verdict. The Cli assembly ships no `InternalsVisibleTo`, so
  a public method is the house test seam — `DescribeDelivery` and `RenderUndeliveredWorkWarning` are public
  for the same reason. `WaveHaltKind.DeliveryRefused` already exists: task 16 added it.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Five requirements, each earned by a defect already shipped here. Encode all five.**

- **Printed BEFORE the verdict.** The `mergeOnSuccess` banner (#340) printed AFTER the green summary
  and was read straight past — an operator concluded a run had shipped when it had not.
- **`git branch --no-merged` is the confirmation, and the report says so.** The report is the harness
  describing itself; the branch state is the fact.
- **The exit code does not change.** A run with a failed wave is a failed run. The exit code answers
  "did the plan complete"; the delivery block answers "what landed". Conflating them is how a green
  tick comes to certify what it did not check.
- **run.json's delivery record tells the truth (round 4, `d39-partial-delivery-record`).** Today
  `RunCommand.DescribeDelivery` derives the #542 record only from the end-of-run merge, so when wave 02
  delivers and wave 03 halts, run.json says `delivered: false` with outcome `not-attempted` while wave
  02 is already on the user's branch. DECIDED: a new `partially-delivered` outcome with
  `delivered: false`. `delivered` stays true only when ALL verified work reached the user's branch, so a
  consumer keyed on it never treats held work as shipped.
- **Nothing that already landed is described as undelivered (review, 2026-09-13).** `DescribeDelivery`
  checks `WhollyGreenButUndelivered` before anything else, and the `*** WORK NOT DELIVERED ***` banner
  says the verified work is "NOT on your checkout". On a waved run whose earlier waves delivered at their
  barriers while the run-end delivery was held or refused, both statements are false about the waves
  that landed. DECIDED: `partially-delivered` wins whenever work both landed and is still held. A
  plan-level terminal gate that fails after earlier waves delivered is the same case (review round 5,
  `d39-barrier-terminal-gate`), and today `RunCommand` returns on that path before writing any delivery
  record at all.

**Pin these behaviours to these EXACT method names:**

- `TheReportNamesDeliveredAndHeldWavesSeparately`
- `TheReportIsPrintedBeforeTheVerdict` — assert ORDER in the captured output, not mere presence.
- `TheReportPointsAtGitBranchNoMerged`
- `DescribeDelivery_APartialDelivery_IsPartiallyDelivered` — a PURE call, with no run and no git:
  construct a `RunReport` whose `WaveDeliveries` (stamped by task 29) records an earlier wave as
  `delivered` and whose run halted at a later wave whose record reads `refused` with outcome
  `DeliveryOutcome.TrialGateFailed` (the refusal a failed trial-tree gate writes, added by task 09), then
  call `RunCommand.DescribeDelivery`. Assert
  `Outcome` is `DeliveryOutcome.PartiallyDelivered`, `Delivered` is `false`, `DeliveredToBranch` and
  `PlanBranch` are both set, and `Reason` names the delivered wave and the held wave. Rejects keeping
  `not-attempted`, and rejects `delivered: true` just because something reached the branch.
- `DescribeDelivery_AGreenRunWhoseRunEndDeliveryWasHeld_IsPartiallyDelivered` — a PURE call. Construct a
  `RunReport` that is wholly green but undelivered (`WhollyGreenButUndelivered`), whose
  `DeliverySuppressingDecision` is a `proceeded-best-guess` recorded at a later wave's task, and whose
  `WaveDeliveries` records an earlier wave as `delivered`. Assert `Outcome` is `PartiallyDelivered`,
  `Delivered` is `false`, `PlanBranch` is set, and `Reason` names the delivered wave AND still names the
  suppressing decision and its subject. Rejects checking `WhollyGreenButUndelivered` first, and rejects a
  reason that drops the decision the operator has to judge.
- `DescribeDelivery_ARefusedRunEndMergeAfterAWaveDelivered_IsPartiallyDelivered` — a PURE call. The
  run-end merge ran and was refused (`MergeOnSuccessOutcome` is `Conflict`, with a
  `MergeOnSuccessDetail`), and `WaveDeliveries` records an earlier wave as `delivered`. Assert `Outcome`
  is `PartiallyDelivered`, `Delivered` is `false`, `Detail` is the merge detail, and `Reason` names the
  delivered wave and the refusal's token, `conflict`. Rejects returning the refusal's own outcome, which
  says nothing about the wave already on the user's branch.
- `TheUndeliveredWorkBanner_NamesTheWavesThatAlreadyDelivered` — a PURE call to
  `RunCommand.RenderUndeliveredWorkWarning` with a `StringWriter`, over a wholly-green-but-undelivered
  report whose `WaveDeliveries` records an earlier wave as `delivered`. Assert the banner names that
  wave's directory. Rejects a banner that tells the operator nothing is on their checkout when one wave
  already is.
- `PartiallyDelivered_RoundTripsThroughTheJournal` — record a `DeliverySection` whose `Outcome` is
  `PartiallyDelivered` through `RunJournal.RecordDelivery`, reload with `RunJournal.LoadOrCreate`, and
  assert the reloaded `Document.Delivery.Outcome` is `PartiallyDelivered` — the pattern
  `RunJournalDeliveryTests.TheDeliveryRecord_SurvivesAReload_SoItAnswersTheQuestionAfterTheRunIsOver`
  uses. Rejects a token added to the writer only: the next resume of that run, which is the resume that
  re-attempts the held wave, would throw reading run.json.
- `ADeliveryRefusedHalt_PrintsItsOwnLabel_NotTheGenericWaveHalt` — call `RunCommand.PrintWaveHalt` with a
  `WaveHalt` whose `Kind` is `WaveHaltKind.DeliveryRefused`, capturing through a `StringConsoleIo`. Assert
  the label line starts with `WAVE DELIVERY REFUSED:`, and the output contains neither `WAVE HALT:` nor
  `GATE FAILED`. Rejects the generic fallback label, and any label that reads as a failed gate over a
  wave whose every check passed.
- `DescribeDelivery_ARunEndDeliveryAfterABarrierDelivery_IsDelivered` — a PURE call. `WaveDeliveries`
  records an earlier wave as `delivered`, the later waves have no `delivered` key, and the run-end merge
  delivered them (`MergeOnSuccessOutcome` is `FastForwarded`, `DeliveredToBranch` set). Assert `Delivered`
  is `true`, `Outcome` is `FastForwarded`, and `Reason` and `PlanBranch` are null, exactly as for a flat
  plan's delivered run. A wave with no `delivered` key is not held when the run-end merge carried it
  there. Rejects counting every wave without a per-wave record as held.
- `DescribeDelivery_AHookRejectionHeldDeliveriesThenTheRunEndMergeLanded_IsDelivered` — a PURE call (review
  round 5, `d39-hooks-untracked-tooling`). `WaveDeliveries` records an earlier wave `delivered`, the next
  wave `refused` with outcome `DeliveryOutcome.HookRejected`, and a later wave `suppressed` with a `Detail`
  naming that rejection; the run-end merge then landed in the user's checkout (`MergeOnSuccessOutcome` is
  `Merged`, `DeliveredToBranch` set). Assert `Delivered` is `true`, `Outcome` is `Merged`, and `Reason` and
  `PlanBranch` are null. A rejecting hook holds deliveries to run end rather than halting, and a run-end
  merge that landed carried every held wave. Rejects counting a `refused` or `suppressed` barrier record as
  held work once the run-end merge landed.
- `TheReportNamesAHookHold_EvenWhenTheRunEndMergeLanded` — PURE calls (final adversarial pass). Build the
  report the row above describes: a `refused` barrier record with outcome `HookRejected` whose `Detail`
  carries the hook's output, a later wave `suppressed` by that rejection, and a run-end merge that landed.
  Call `RunCommand.RenderWaveDeliveryReport` with a `StringWriter`, and assert the output names the
  rejecting wave's directory, the hook's detail, and the later held wave's directory. Then call
  `RunCommand.DescribeDelivery` on the same report and assert it still reads `Delivered` true, `Outcome`
  `Merged` and a null `Reason`: the hold shows in the report only, and the delivery record does not
  change. Rejects a run that reads green and delivered while the hook silently held back every incremental
  delivery.
- `ATerminalGateFailureAfterAWaveDelivered_StillRecordsPartiallyDelivered` — a PURE call (review round 5,
  `d39-barrier-terminal-gate`). `WaveDeliveries` records an earlier wave `delivered`, every task succeeded,
  the plan-level terminal gate did not pass (`terminalGatePassed: false`), and the final wave has no
  `delivered` key: the final wave always delivers at run end, which the failed gate withheld. Assert
  `Outcome` is `PartiallyDelivered`, `Delivered` is `false`, and `Reason` names the delivered wave and the
  failed terminal gate. Rejects the never-attempted terminal-gate reason, which says nothing about the wave
  already on the user's branch.
- `ATerminalGateFailureAfterAWaveDelivered_WritesTheDeliveryRecordBeforeReturning` — drive a waved run
  through `CommandFactory.BuildRootCommand(io)` over a temp repo, the way the report-order rows do: an
  earlier wave marked `delivers: true` delivers at its barrier, the final wave's tasks succeed, and a
  plan-level `guardrails/` check exits 1. After the run, reload the journal with `RunJournal.LoadOrCreate`
  and assert `Document.Delivery` is not null and its `Outcome` is `PartiallyDelivered`. Rejects writing the
  record after `RunCommand`'s terminal-gate early return, which today skips the write entirely, so
  run.json says nothing about a wave already on the user's branch.
- `AFailedWaveDoesNotChangeTheExitCode`
- `AFullyDeliveredRunReadsAsTodayDoes` — the never-weaker requirement: a plan marking no wave must
  produce the output it produces today.

`DescribeDelivery_ARunEndDeliveryAfterABarrierDelivery_IsDelivered`,
`DescribeDelivery_AHookRejectionHeldDeliveriesThenTheRunEndMergeLanded_IsDelivered`,
`AFailedWaveDoesNotChangeTheExitCode` and `AFullyDeliveredRunReadsAsTodayDoes` are declared EXEMPT from the
red census. `DescribeDelivery` returns a landed run-end merge as delivered without reading
`WaveDeliveries`, whatever the barrier records say; a run with a failed wave already exits 2; and a plan
that marks no wave already prints today's output. So correct tests of all four are green on arrival. Write
them to assert the guarantee, not to fail. They must still exist, and task 19's forward census requires
all sixteen Passed.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong. Capture the output whose ORDER you assert through a
`StringConsoleIo` handed to `CommandFactory.BuildRootCommand(io)` — never by redirecting `Console.Out`,
which is process-wide.

The other twelve tests MUST COMPILE and FAIL. Do NOT implement the report, the delivery record, the
banner or the label.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/WaveDelivery/PartialDeliveryReportTests.cs`,
`src/Guardrails.Core/Journal/JournalModel.cs` and `src/Guardrails.Cli/Commands/RunCommand.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths.
An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}`
to the state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
