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

- When some waves delivered and verified work is still held on the plan branch, return
  `DeliveryOutcome.PartiallyDelivered` with `Delivered = false`. `delivered` stays true only when ALL
  verified work reached the user's branch.
- Set both `DeliveredToBranch` and `PlanBranch`.
- Make `Reason` name the delivered waves and the held waves.
- Add the `partially-delivered` token to `JournalJson.DeliveryOutcomeToken` AND to
  `DeliveryOutcomeConverter.Read`. The converter throws on an unknown member, and a record that writes
  but cannot be read back breaks the next resume (#625).

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Commands/RunCommand.cs`,
`src/Guardrails.Core/Journal/JournalModel.cs`, and `src/Guardrails.Core/Journal/JournalJson.cs`. After
this task completes, the harness runs a `git diff` membership check and rejects any edit outside these
paths. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error
caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
