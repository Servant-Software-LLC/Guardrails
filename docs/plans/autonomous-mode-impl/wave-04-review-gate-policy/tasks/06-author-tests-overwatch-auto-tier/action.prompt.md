## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/06-author-tests-overwatch-auto-tier` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the **overwatcher `auto`-tier gate** (issue #361 Phase 4,
doc 12 §9 Phase 4; doc 11 §6/§9.6), plus the MINIMAL stub. The repo uses **xUnit (xunit.v3)**; mirror the
shipped `tests/Guardrails.Core.Tests/OverwatchClassifierTests.cs` and the integration `OverwatchTests` for
shape (how they construct `Overwatch`, an `IOverwatchInteraction`, an `OverwatchProposal`, and the
guidance/budget `OverwatchFixOp` levers).

**Anchor on these REAL shipped symbols (grep them — durable markers, not line numbers):**
- `Overwatch` (`src/Guardrails.Core/Execution/Overwatch.cs`) — ctor today
  `Overwatch(IPromptRunner? diagnoseRunner, NeedsHumanTriage? terminalTriage, AutonomyPolicy policy,
  IOverwatchInteraction? interaction = null)`; the private `Decide(...)` maps the proposal onto the policy.
  Today `auto` **degrades to prompt** (grep the comment `prompt (and, in v1, auto — which degrades to
  prompt)`): the prompt/auto branch calls `_interaction.ConfirmApply(...)`.
- `IOverwatchInteraction` (grep it) — `ConfirmApply(...)` returns `OverwatchInteractionResult`
  (`Apply` / `Declined` / ...); `IOverwatchInteraction.NonInteractive` halts. A recording fake (that
  records whether `ConfirmApply` was called, and can return `Apply`) is what distinguishes SILENT
  auto-apply (ConfirmApply NOT called) from a prompt-then-apply.
- `OverwatchFixClassifier.Classify(...)` → `OverwatchAuthorityClass` (`Allowlist` / `Denylist` / `Default`).
  The ALLOWLIST levers are the ephemeral guidance-injection + `maxTurns`/`retries`/`timeoutSeconds` budget
  ops; the DENYLIST is any guardrail/preflight-body or `writeScope`/`scope`/`dependsOn`/`integrationGate`
  edit (grep the classifier).

Write two artifacts (both in scope):

1. **The test file** `tests/Guardrails.Core.Tests/OverwatchAutoTierTests.cs` — tests that must FAIL against
   the stub. Use a recording `IOverwatchInteraction` fake. Assert:
   - **auto + `autonomyBlockPresent: true` + a sanctioned ALLOWLIST lever ⇒ SILENT auto-apply.** The
     overwatcher GRANTS the retry WITHOUT calling `ConfirmApply` (the block-present auto-tier applies the
     action/budget lever with no prompt). This is the new behavior — it FAILS against the stub.
   - **auto + `autonomyBlockPresent: false` ⇒ STILL degrades to prompt (byte-identical back-compat).** The
     REQUIRED anti-Option-(c) guard (doc 12 §9 Phase 4): with `AutonomyPolicy.Auto` but NO `autonomy` block,
     `ConfirmApply` IS called exactly as today (a non-interactive fake ⇒ honest halt). This asserts the
     gate keys on the BLOCK PRESENCE, not on `autonomyPolicy: auto` alone.
   - **a `Denylist` op never auto-applies, even with `autonomyBlockPresent: true`.** A verdict-surface
     (`OverwatchAuthorityClass.Denylist`) change is propose-only at EVERY tier — it must route to
     human/`ConfirmApply` (or NonGrant), never silent auto-apply. The DENYLIST stays permanently human-only.

2. **The minimal stub**: add a TRAILING OPTIONAL param `bool autonomyBlockPresent = false` to the
   `Overwatch` constructor (AFTER the existing `interaction` param, so every current caller — including
   `SchedulerFactory` — still compiles by defaulting it). **Do NOT store it in a field yet and do NOT
   change `Decide`** (an unused private field would trip `CS0169` under the repo's
   `TreatWarningsAsErrors=true`; the constructor may simply accept-and-ignore the param for now). The gate
   itself is the implementation task's job — leaving `Decide` unchanged is what makes the auto-apply test
   FAIL (it still prompts) while the back-compat test already passes (it must survive unchanged).

   The tests MUST COMPILE and FAIL (not compiling is a mistake). Confirm the whole solution still builds
   after adding the optional param (`dotnet build Guardrails.sln -c Debug`) — a required param would break
   `SchedulerFactory`'s existing `new Overwatch(...)` call.

**In-attempt regression check (issue #253 — do NOT skip):** run ONLY your targeted filter —
`dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~OverwatchAutoTierTests"`. Do NOT run
the full unfiltered `dotnet test tests/Guardrails.Integration.Tests` (fixture leak, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/OverwatchAutoTierTests.cs` and `src/Guardrails.Core/Execution/Overwatch.cs`.
After this task the harness runs a `git diff` check and rejects any edit outside these paths — including
`SchedulerFactory.cs` (task 07), `OverwatchFixClassifier.cs`, or `IOverwatchInteraction`. An out-of-scope
edit fails the task immediately and consumes a retry. If a shipped type is missing a member you need, do
NOT edit it — write `{"needsHuman": "<what is missing>"}` and stop.
