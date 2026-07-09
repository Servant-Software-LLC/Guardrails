# Model tiering — Stage 3: Dynamic behavior (escalation ladder + interactive steering)

> **Design of record: [`13-model-tiering.md`](13-model-tiering.md)** — the contract-locked
> decisions live there; where this brief and the DoR differ, the DoR wins (notably: the #228
> escalation-budget question is RESOLVED — same retry pool, no reset — and #231's unattended
> threshold behavior rides the shared `autonomyPolicy` `routing` boundary, DoR §7/§8).
> "Stage" here means a sequential design phase of this epic — NOT a #254 runtime wave (SSOT §14).

Part of the model-tiering epic (#201). This is stage 3 of 3 — **depends on stage 2
(`model-tiering-consumers.md`, issues #226+#227) having landed**: both tasks here modify or
consult the per-attempt resolution step and the budget probes stage 2 introduces. Covers issues
**#228** (escalation ladder) and **#231** (interactive routing steering).

## Context

Stage 2's #226 resolves a task's tier to a concrete (provider, model, effort) fresh at each attempt
launch; #227 gives that resolution live budget/limit visibility per provider. This stage is the
first to make resolution **behavior-dependent** rather than purely state-dependent: #228 reacts to
a task's own attempt history, and #231 reacts to a live human decision. Both are already recorded
as design (not yet implementation) on #201's "Resolution timing" section:

> The escalation ladder is the safety net when even the tag is wrong or absent. A task starts at
> its tier's currently-resolved provider; on a guardrail-failed attempt, the harness escalates the
> NEXT attempt to a stronger tier automatically. No upfront difficulty judgment is required — the
> retry loop discovers it empirically.

## The ask

### #228 — Escalation ladder ("graduate to a stronger provider on repeated attempts")
1. A task's first attempt resolves at its tagged tier (stage 1/2 mechanism, unchanged). If that
   attempt's guardrails fail, the task's **next** attempt resolution (#226) is asked to escalate
   one tier stronger, rather than re-resolving at the same tier.
2. Escalation is scoped to that one task's own retry loop — it must not affect sibling tasks'
   resolutions or the plan-wide default tier.
3. Decide and implement: does an escalated attempt reset the retry budget, or consume from the
   same pool as a same-tier retry? (Open question carried from #201 — resolve it here, in code and
   in the SSOT.)
4. Escalation must be visible in the attempt log / run report (extends #198/#230's existing model
   logging) — a human reviewing a run should be able to see "task X escalated from local to
   frontier on attempt 3."
5. Cap the ladder at the strongest tier actually registered/available (#224) — never invent a
   stronger tier than what's configured.
6. Before escalating INTO a stronger tier, consult that tier's budget probe (#227) — escalating
   into a provider that's itself already near its limit is not obviously better than staying put;
   at minimum, this should be visible/loggable, even if the escalation proceeds anyway.
7. Compose with the existing honest-halt precedent: if even the strongest available tier's attempt
   fails, the task follows the normal needs-human path exactly as today — the ladder changes WHICH
   model gets tried, never whether a genuinely-stuck task eventually surfaces to a human.

### #231 — Interactive routing steering (threshold prompts + ambient override)
1. **Ambient/anytime steering**: a standing user instruction (e.g. "lean hard on local inference
   right now") that biases resolution (#226) without editing the plan or its tiers. Needs a
   concrete surface — a CLI flag, a session-scoped setting, or a config override the resolution
   step reads alongside the registry and probed budget state (#227).
2. **Threshold-triggered prompt at `/plan-breakdown` time**: before task-to-tier assignment is
   finalized (stage 1, #225), check limits across registered providers (#227); if one is running
   low, surface routing-strategy options to the human before generating the DAG.
3. **Threshold-triggered prompt at harness `run` time**: mid-run, when the projected remaining
   work would blow through a provider's rate window, pause and present concrete options (see the
   worked example on #201/#231).
4. Both prompts are genuine decision points — present real options, wait for a real answer, never
   silently auto-decide. Define the unattended/CI fallback explicitly (a sensible default + a loud
   log line, not a silent hang).
5. The "will this blow the limit" projection needs some estimate of remaining work's cost — a
   rough per-tier average from prior attempts in the same run is sufficient; do not over-engineer
   a precise forecast.

## Acceptance

- A task whose first attempt fails its guardrails demonstrably resolves its next attempt to a
  stronger tier, visible in the attempt log; a task that never fails stays at its original tier
  throughout.
- An ambient steering instruction measurably changes which tier a subsequent run's tasks resolve
  to, without any plan/task-file edit.
- A simulated near-limit budget state (stubbed probe) triggers the run-time threshold prompt with
  real, selectable options, and the run proceeds according to the choice made.
- An unattended run past a threshold does not hang — it takes the documented default and logs it
  loudly.

## Stack

.NET 8 / xUnit v3 for `Guardrails.Core`/`Guardrails.Cli` (escalation logic, steering surface,
threshold-check + prompt UX). Verification: `dotnet test tests/Guardrails.Core.Tests` +
`tests/Guardrails.Integration.Tests` for the escalation/steering behavior; a scripted or manual
check for the interactive-prompt UX (both at `/plan-breakdown` and `guardrails run` call sites).

## Related
#201 (epic), #226/#227 (stage 2 — hard prerequisite), #224 (registry — defines the ladder's rungs
and what's available to steer toward), #228/#231 (this stage's issues), #198/#230 (existing model
logging + cost accounting this stage's visibility requirements extend).
