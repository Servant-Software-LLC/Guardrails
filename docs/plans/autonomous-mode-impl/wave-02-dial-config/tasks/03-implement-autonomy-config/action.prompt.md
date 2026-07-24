## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-02-dial-config/03-implement-autonomy-config` — NOT the stableId. (This task publishes nothing to
  state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the `autonomy` config block (issue #361 Phase 2) by filling REAL logic over the stubs the
previous task authored. The design of record is `docs/plans/12-autonomous-mode.md` §3.3/§3.4/§3.5 and
the decided values §10 F/G/I/N (read first). Make the authored `AutonomyConfigTests` pass WITHOUT
editing them.

Implement:
- `AutonomyConfig` (in `src/Guardrails.Core/Model/AutonomyConfig.cs`): `escalationThreshold`
  (`EscalationThreshold` ordered enum `low < moderate < high < critical`), `gateThresholds`
  (`needs-human` / `wave-checkpoint` as criticality levels, `review-gate` as the
  `escalate` / `proceed-unreviewed` acknowledgment — NOT a criticality level), `blockerRetry`
  (`maxAttempts`, `totalWaitSeconds`), `maxJudgeWidenings`.
- Parsing (`RawRunConfig` in `RawManifests.cs` + the mapping in `PlanLoader.cs`): map the raw `autonomy`
  block into `RunConfig.Autonomy`; apply the DEFAULTS when the block is present but a field is omitted —
  `escalationThreshold: high`, `blockerRetry { maxAttempts: 5, totalWaitSeconds: 900 }`,
  `maxJudgeWidenings: 3`. When the block is ABSENT, `RunConfig.Autonomy` stays null/inert and the rest
  of `RunConfig` is byte-identical to today (the back-compat guarantee — do not shift any existing
  default).
- `autonomy` COMPOSES with `autonomyPolicy`; it does not redefine or reinterpret it. Leave
  `autonomyPolicy` parsing untouched. Do NOT add validation here (GR2039/GR2040 are a separate task) and
  do NOT wire any CLI flags (a separate task).

**Scope boundary (harness-enforced):** Write only under `src/Guardrails.Core/Model/` and to
`src/Guardrails.Core/Loading/RawManifests.cs` + `src/Guardrails.Core/Loading/PlanLoader.cs`. Do NOT edit
the authored tests, `PlanValidator.cs`, or `DiagnosticCodes.cs` — if a test is genuinely wrong, emit
`{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit fails the write-scope check and
burns a retry).

Completion criteria (your guardrail checks these): `AutonomyConfigTests` and the existing
`AutonomyPolicyConfigTests` / `LogsAndRunConfigTests` in `tests/Guardrails.Core.Tests` all pass.
