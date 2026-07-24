## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/07-implement-overwatch-auto-tier` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

**Implement the overwatcher `auto`-tier gate + wire the block-presence signal** (issue #361 Phase 4,
doc 12 §9 Phase 4; doc 11 §6/§9.6). Task 06 added the optional `autonomyBlockPresent` ctor param and the
failing tests. Your slice makes them pass by (a) implementing the gate in `Overwatch.Decide` and (b) wiring
`SchedulerFactory` to pass the real block-presence. Make the authored `OverwatchAutoTierTests` pass by
IMPLEMENTING (do NOT weaken them).

**Architecture caveat (#203) — verify before you build on it.** Cite DURABLE markers (grep the symbols),
never line numbers. Confirm each still holds in the materialized tree (which now includes task 06's ctor
param):
- `Overwatch.Decide` (`src/Guardrails.Core/Execution/Overwatch.cs`) — grep the comment `prompt (and, in v1,
  auto — which degrades to prompt)`: that is the branch where, today, prompt AND auto both call
  `_interaction.ConfirmApply(...)`. The floors ABOVE it (halt / permission-wall / doomed /
  no-sanctioned-change) must remain unchanged — the gate is added only for the sanctioned-allowlist grant.
- The `autonomyBlockPresent` ctor param task 06 added — STORE it in a `private readonly bool` field now
  (it was accept-and-ignore in the stub) and READ it in the gate (this is what removes the unused-param /
  keeps the `TreatWarningsAsErrors` build green).
- `SchedulerFactory.CreateExecutor` (`src/Guardrails.Core/Execution/SchedulerFactory.cs`) — grep
  `new Overwatch(` (it passes `plan.Config.AutonomyPolicy` today). Add the block-presence argument
  `plan.Config.Autonomy is not null`.

Implement:
- **Overwatch.Decide gate:** when `_policy == AutonomyPolicy.Auto` **AND** `_autonomyBlockPresent` **AND** a
  sanctioned ALLOWLIST change exists (the existing `guidance`/`budget` levers) **AND** the op is NOT a
  floor / permission-wall / doomed / Denylist — GRANT the retry with the sanctioned change **WITHOUT
  calling `_interaction.ConfirmApply`** (silent auto-apply; realize the action/budget half of overwatcher
  v2 bet #6). Record it with the appropriate decision token (the shipped `auto-applied` token is the
  natural fit — reuse it; do not invent one).
- **Back-compat (the load-bearing anti-Option-(c) guarantee):** when `_policy == Auto` but
  `_autonomyBlockPresent` is **false** — OR the policy is `prompt` — keep the EXACT existing behavior:
  call `_interaction.ConfirmApply(...)` (a non-interactive interaction ⇒ honest halt). Byte-identical to
  today. Do NOT auto-apply.
- **DENYLIST stays human-only:** a verdict-surface (`OverwatchAuthorityClass.Denylist`) op is propose-only
  at EVERY tier — it must NEVER reach the silent auto-apply branch, block present or not. (The existing
  classifier already keeps denylist ops off the `guidance`/`budget` levers; make sure the new gate cannot
  route around that.)
- **SchedulerFactory:** change the `new Overwatch(...)` construction to pass `plan.Config.Autonomy is not
  null` as `autonomyBlockPresent`, so the gate engages ONLY when the plan carries an `autonomy` block
  (not `autonomyPolicy: auto` alone).

Do NOT change the classifier, the interaction seam, or the config types. This task is the gate + its wiring.

**In-attempt regression check (issue #253 + #374 — do NOT skip, and run it PLAINLY):** run ONLY your
targeted filter, via the **Bash tool**, as a **plain** command — no `&` call-operator, no pipe, no
`2>&1 |`, not the PowerShell tool (issue #374 blocks those as "multiple operations requiring approval"):

    dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~OverwatchAutoTierTests"

(and `dotnet build Guardrails.sln -c Debug` to confirm the whole solution still compiles).

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Overwatch.cs` and
`src/Guardrails.Core/Execution/SchedulerFactory.cs`. The harness runs a post-action `git diff` membership
check and REJECTS any edit outside these two paths — including the authored test, `OverwatchFixClassifier.cs`,
or `Scheduler.cs` (task 05/09). An out-of-scope edit fails the task immediately and consumes a retry. If the
gate genuinely needs a change to another file, do NOT edit it — write `{"needsHuman": "<what is missing>"}`
to the state-out path and stop.

Completion criteria (your guardrails check these): `Overwatch.cs` gates on `autonomyBlockPresent`,
`SchedulerFactory.cs` passes `plan.Config.Autonomy is not null` into the Overwatch construction, and the
authored `OverwatchAutoTierTests` pass.
