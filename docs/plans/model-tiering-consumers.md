# Model tiering — Stage 2: Consumers (resolution, budget probes, review check, cost accounting)

> **Design of record: [`13-model-tiering.md`](13-model-tiering.md)** — the contract-locked
> decisions live there; where this brief and the DoR differ, the DoR wins (notably: probes
> ADVISE ranking and never gate a launch — DoR §6.3 revises this brief's #226 item 4).
> "Stage" here means a sequential design phase of this epic — NOT a #254 runtime wave (SSOT §14).

Part of the model-tiering epic (#201). This is stage 2 of 3 — **depends on stage 1
(`model-tiering-foundation.md`, issues #224+#225) having landed**: every task here reads either
the provider registry (#224) or the difficulty tier (#225). Covers issues **#226** (runtime tier
resolution), **#227** (budget/limit probes), **#229** (guardrails-review model-appropriateness
check), and **#230** (cost/token accounting by tier).

## Context

Today, per-attempt model resolution is a simple two-level fallback:
`TaskExecutor.cs:1032` calls `PromptExecutionSupport.ResolveModelForDisplay(task.Action.Model,
runnerModel)`, where `runnerModel` comes from the runner config's `config.Settings.Model`
(`TaskExecutor.cs:1027`) and `task.Action.Model` is the per-task override (#200). This stage inserts
a new resolution step **between** those two: task-level explicit override (highest precedence,
unchanged) → **tier-based dynamic resolution** (new) → runner-config default (unchanged fallback
for a task with no tier and no override, though stage 1 makes an untagged task fall back to the
plan-wide default tier before it ever reaches the runner default).

Per-attempt model logging already exists (#198, shipped) — #230's cost accounting is primarily an
**aggregation** over that existing data plus the new tier field, not new data collection.

## The ask

### #226 — Runtime tier → (provider, model, effort) resolution at attempt-launch time
1. Immediately before each attempt launch (including retries — this must re-run every time, not
   once per task), resolve the task's tier (#225) to a concrete (provider, model, effort) by
   consulting the CURRENT provider registry (#224), each candidate model's routing guidance
   (stage 1's guidance field), and live budget/limit state (#227, this stage).
2. `action.model`/`action.effort`, when set, bypasses this resolution entirely — explicit always
   wins (no behavior change to the existing override path).
3. Record the resolved (provider, model, effort) in the attempt's log header, extending the
   existing per-attempt model logging (#198) with the provider dimension.
4. If no provider can currently serve the tier (e.g. every registered provider exhausted), fail
   resolution with an honest, actionable message — never silently pick something inappropriate.

### #227 — Budget/limit probes per provider
1. For each registered provider (#224), add a way to query its CURRENT usage/limit state where its
   API/CLI exposes one — e.g. Claude's weekly Max-plan % and 5-hour rolling-window %, OpenRouter's
   remaining credit balance, a local endpoint's availability/load.
2. Per-provider-kind implementation (no universal usage API exists) — degrade to "unknown" rather
   than fail the run when a provider exposes nothing.
3. Cache/rate-limit the probes themselves so they don't become their own latency or rate-limit
   consumer on every attempt.
4. Expose the probed state somewhere inspectable (run report, or a `guardrails providers status`
   command).

### #229 — guardrails-review model-appropriateness check
1. Flag a prompt-action task (or surviving judge-guardrail) with **neither** a difficulty tag
   (#225) **nor** an explicit `action.model`/`action.effort` override — the safety net for a
   human-added task the original breakdown never classified.
2. Flag a tier **mismatch**: a hard/security-critical task (touching the retry/journal contract,
   cross-module architecture, or anything the catalogue already treats as high-risk) tagged for a
   weak tier; or a trivial/mechanical task tagged for a frontier-only tier.
3. Advisory findings only (the skill's read-only-by-default posture) — never a silent auto-fix.
4. If a repo's `task.json` predates stage 1 (no tier field at all), skip gracefully rather than
   erroring.

### #230 — Cost/token accounting split by tier
1. The final run summary breaks down token/cost spend by tier (e.g. "frontier: 42k tokens / $X,
   local: 180k tokens / $0"), sourced from the per-attempt model/provider log (#198) plus the tier
   field (#225) — aggregation over existing data, not new collection.
2. Degrade to token counts only where $ pricing isn't known/configured for a provider (a local
   model may have no meaningful $ cost).

## Acceptance

- A real multi-task plan with a mix of tiers resolves each attempt to a concrete provider/model
  that the attempt log records, distinct from the runner-config default when the tier calls for
  something else.
- Simulating an exhausted/low provider (or a stubbed probe response) demonstrates resolution
  failing honestly rather than silently misrouting.
- `guardrails-review` on a plan with a hand-added, untagged task flags it.
- The run summary shows a real per-tier breakdown on a plan that used more than one tier.

## Stack

.NET 8 / xUnit v3 for `Guardrails.Core`/`Guardrails.Cli` (resolution, probes, run-report
aggregation). `.claude/skills/guardrails-review/SKILL.md` for the appropriateness check (a
`guardrails-skill-author` task). Verification: `dotnet test tests/Guardrails.Core.Tests` +
`tests/Guardrails.Integration.Tests` for the resolution/probe behavior; the guardrails-review
golden fixtures for #229.

## Related
#201 (epic), #224/#225 (stage 1 — hard prerequisite), #198 (shipped per-attempt model logging,
extended here), #226/#227/#229/#230 (this stage's issues), stage 3
(`model-tiering-dynamic-behavior.md`, depends on #226 and #227 from this stage).
