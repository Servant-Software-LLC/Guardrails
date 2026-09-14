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
**Stub:** add one member, `PartiallyDelivered`, to `DeliveryOutcome` in
`src/Guardrails.Core/Journal/JournalModel.cs`, so the tests compile. Do NOT add its token:
`JournalJson`'s converter throws on an unknown member and task 19 owns the `partially-delivered`
token, so no test in this suite may serialize the new member.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Four requirements, each earned by a defect already shipped here. Encode all four.**

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

**Pin these behaviours to these EXACT method names:**

- `TheReportNamesDeliveredAndHeldWavesSeparately`
- `TheReportIsPrintedBeforeTheVerdict` — assert ORDER in the captured output, not mere presence.
- `TheReportPointsAtGitBranchNoMerged`
- `DescribeDelivery_APartialDelivery_IsPartiallyDelivered` — a PURE call, with no run and no git:
  construct a `RunReport` whose `WaveDeliveries` (stamped by task 29) records an earlier wave as
  `delivered` and whose run halted at a later wave, then call `RunCommand.DescribeDelivery`. Assert
  `Outcome` is `DeliveryOutcome.PartiallyDelivered`, `Delivered` is `false`, `DeliveredToBranch` and
  `PlanBranch` are both set, and `Reason` names the delivered wave and the held wave. Rejects keeping
  `not-attempted`, and rejects `delivered: true` just because something reached the branch.
- `AFailedWaveDoesNotChangeTheExitCode`
- `AFullyDeliveredRunReadsAsTodayDoes` — the never-weaker requirement: a plan marking no wave must
  produce the output it produces today.

`AFailedWaveDoesNotChangeTheExitCode` and `AFullyDeliveredRunReadsAsTodayDoes` are declared EXEMPT from
the red census: a run with a failed wave already exits 2 on today's code, and a plan that marks no wave
already prints today's output, so correct tests of both are green on arrival. Write them to assert the
guarantee, not to fail. They must still exist, and task 19's forward census requires all six Passed.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong. Capture the output whose ORDER you assert through a
`StringConsoleIo` handed to `CommandFactory.BuildRootCommand(io)` — never by redirecting `Console.Out`,
which is process-wide.

The other four tests MUST COMPILE and FAIL. Do NOT implement the report or the delivery record.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/WaveDelivery/PartialDeliveryReportTests.cs` and
`src/Guardrails.Core/Journal/JournalModel.cs`. After this task completes, the harness runs a `git diff`
membership check and rejects any edit outside these paths. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another file,
do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
