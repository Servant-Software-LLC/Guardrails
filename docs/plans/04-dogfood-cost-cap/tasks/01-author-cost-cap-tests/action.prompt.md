## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Author tests that encode the per-run cost cap BEFORE it is implemented. The feature
(see `docs/plans/04-dogfood-cost-cap.md`): an optional `maxCostUsd` field in
`guardrails.json`; when the cumulative journaled cost reaches/exceeds it, the harness
stops launching new attempts and marks remaining work `needs-human` with reason
"cost cap reached". A non-positive `maxCostUsd` is a validation error GR2010.

Write THREE groups of failing tests, using the existing conventions in each project
(xUnit.v3; `TestContext.Current.CancellationToken` where a token is needed — xUnit1051
is an error here):

1. Config parsing — in `tests/Guardrails.Core.Tests/` (a new file, e.g.
   `CostCapConfigTests.cs`): a `guardrails.json` containing `"maxCostUsd": 1.50` loads
   so that `RunConfig.MaxCostUsd == 1.50m`, and an absent field leaves it null. Follow
   `PromptRunnerConfigTests.cs` for the on-disk plan-folder pattern.

2. Validation — in `tests/Guardrails.Core.Tests/` (e.g. add to a new
   `CostCapValidatorTests.cs`): a plan whose config has `maxCostUsd <= 0` (test both 0
   and a negative) produces a diagnostic with code `DiagnosticCodes.CostCapNonPositive`
   (the GR2010 constant you reference here will be added by the implementation task).
   A positive cap produces no such diagnostic. Follow `PlanValidatorTests.cs`.

3. Scheduler halt — in `tests/Guardrails.Core.Tests/` (e.g. `CostCapSchedulerTests.cs`),
   using the in-memory scheduler test style in `SchedulerTests.cs` with a fake executor
   and a fake `ISchedulerJournal`: with a `maxCostUsd` cap already exceeded by journaled
   cost, a not-yet-run task is NOT executed — it settles `needs-human` with a reason
   containing "cost cap reached", and its dependents become `blocked`. Below the cap, all
   tasks run normally. Read `SchedulerTests.cs` and `ISchedulerJournal.cs` first to match
   the existing fakes; the journal fake must let a test preload a cumulative cost.

Reference the constant name `DiagnosticCodes.CostCapNonPositive` and the property
`RunConfig.MaxCostUsd` so the tests compile against the implementation task's additions —
BUT these tests MUST NOT COMPILE OR MUST FAIL against the current code, because neither
`MaxCostUsd` nor `CostCapNonPositive` nor the scheduler check exists yet. That failure is
the point (it proves the tests encode unbuilt behavior). Do NOT implement the feature.
Do NOT edit any non-test file.

After writing all three test files, record their content hashes so a downstream
guardrail can verify they were not modified by the implementation task.
For each file, run `git hash-object <path>` (e.g. Bash with
  git hash-object tests/Guardrails.Core.Tests/CostCapConfigTests.cs
); capture the trimmed hex output. Then write this JSON to the path in
GUARDRAILS_STATE_OUT (which the harness appended to this prompt):

{
  "01-author-cost-cap-tests": {
    "testFileHashes": {
      "tests/Guardrails.Core.Tests/CostCapConfigTests.cs": "<hex>",
      "tests/Guardrails.Core.Tests/CostCapValidatorTests.cs": "<hex>",
      "tests/Guardrails.Core.Tests/CostCapSchedulerTests.cs": "<hex>"
    }
  }
}
