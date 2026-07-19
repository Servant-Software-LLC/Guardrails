## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-02-dial-config/02-author-tests-autonomy-config` — NOT the stableId. (This task publishes nothing
  to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the criticality dial's **`autonomy` config block** (issue
#361, doc 12 §3.3/§3.4/§3.5; decided values §10 F/G/I/N), plus the MINIMAL stubs to compile. The repo
uses **xUnit (xunit.v3)**; mirror `tests/Guardrails.Core.Tests` (see `AutonomyPolicyConfigTests.cs` /
`LogsAndRunConfigTests.cs` for how a `guardrails.json` is loaded into `RunConfig`).

Write these artifacts (all in scope):

1. **The test file** `tests/Guardrails.Core.Tests/AutonomyConfigTests.cs` — tests that must FAIL against
   the stubs:
   - **Populated block parses**: a `guardrails.json` with an `autonomy` block
     (`escalationThreshold`, `gateThresholds { needs-human, wave-checkpoint, review-gate }`,
     `blockerRetry { maxAttempts, totalWaitSeconds }`, `maxJudgeWidenings`) loads into a non-null
     `RunConfig.Autonomy` with every field mapped.
   - **Defaults**: an `autonomy` block present with fields OMITTED yields `escalationThreshold` = `high`,
     `blockerRetry` = `{ maxAttempts: 5, totalWaitSeconds: 900 }`, `maxJudgeWidenings` = `3`.
   - **Ordered enum**: `low < moderate < high < critical` compares in that order (the dial value is "the
     lowest criticality that still escalates").
   - **Inert-by-default / byte-identical (REQUIRED, doc 12 §3.2)**: a `guardrails.json` with NO `autonomy`
     block loads with `RunConfig.Autonomy` null/inert, and the resulting `RunConfig` is otherwise
     identical to the same config today (assert the block-absent load equals the pre-existing behaviour —
     no field shifted). This is the load-bearing back-compat guarantee.
   - `autonomy` composes with, and never redefines, `autonomyPolicy` (both can be set independently).

2. **The minimal stubs**: `src/Guardrails.Core/Model/AutonomyConfig.cs` (the `AutonomyConfig` record +
   the `EscalationThreshold` ordered enum + nested `gateThresholds`/`blockerRetry` shapes — DATA); add
   the optional `Autonomy` property to `src/Guardrails.Core/Model/RunConfig.cs`; add the raw field to
   `RawRunConfig` in `src/Guardrails.Core/Loading/RawManifests.cs`; and in
   `src/Guardrails.Core/Loading/PlanLoader.cs` stub the raw→`Autonomy` MAPPING so that a config WITHOUT
   the block still loads inertly (returns null Autonomy) but a config WITH the block hits a
   `throw new NotImplementedException();` — so the block-absent inert test passes trivially while the
   populated-block/defaults tests FAIL (TDD red) and existing config loading is unbroken.

   The tests MUST COMPILE and FAIL (not compiling is a mistake). Do NOT implement the real parse.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/AutonomyConfigTests.cs`, `src/Guardrails.Core/Model/AutonomyConfig.cs`,
`src/Guardrails.Core/Model/RunConfig.cs`, `src/Guardrails.Core/Loading/RawManifests.cs`, and
`src/Guardrails.Core/Loading/PlanLoader.cs`. After this task the harness runs a `git diff` check and
rejects any edit outside these paths. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error from a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.
