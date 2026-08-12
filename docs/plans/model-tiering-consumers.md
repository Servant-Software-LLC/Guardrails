# Model tiering — Stage 2: Consumers (resolution, budget probes, review check, cost accounting)

> **Design of record: [`17-model-tiering.md`](17-model-tiering.md)** — the contract-locked
> decisions live there; where this brief and the DoR differ, the DoR wins.
> **v1/v2 re-bucket (DoR revision):** of this stage's four issues, **v1** ships **#226 as a
> STATIC resolver** (no probe consultation, no ladder-awareness — DoR §6.1–§6.3), **#229** (review
> check), and **#230 as a per-tier spend line** (#230-lite, DoR §9.3). **#227 budget/limit probes
> are DEFERRED to a named v2 bet** (DoR §6.4, §10) — do not build the probe layer in v1. "Stage"
> here means a sequential design phase of this epic — NOT a #254 runtime wave (SSOT §14).
> **DoR revision 3 adds two things to this stage** (from the reviewed verifier charter): the
> **costly floor** inside a single shared candidacy predicate (DoR §6.2 — the harness may never
> auto-select a `costly: true` block, and validation + runtime must agree on that), and the
> **verifier route** (DoR §6.5 — a judge guardrail resolves ≥ its actor, with a *strength* bump
> when the actor is weak, surfaced as an **advisory** at BOTH boundaries — a startup preflight AND
> a per-attempt JIT re-check — and via #229, never blocking). Candidate ordering is **ascending `strength`**, not `routing.rank`.

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
5. **The costly floor + one candidacy predicate (DoR §6.2, revision 3).** `Candidates(R)` = blocks
   with `routing` present ∧ `R ∈ routing.tiers` ∧ **not `costly: true`**, ordered by **ascending
   `strength`** (unspecified last), ties by declaration order. Write it **once** and use it from
   the resolver, the GR2046 validation, and the `no-route` path — if validation and runtime
   disagree about which blocks serve a rung, validation passes and every task of that rung dies at
   runtime.
6. **The verifier route (DoR §6.5, revision 3 — charter `model-tiering-verifier.charter.md`).** A
   prompt-**judge** guardrail resolves its own (provider, model, effort) alongside the actor:
   frontmatter `tier`/`runner` wins; otherwise the judge takes the **actor's rung**, and when the
   actor is *weak* (declared `strength`, else the `kind != "claude"` fallback) the judge is the
   **weakest candidate at that rung whose `strength` is strictly greater** — a **strength** bump,
   never a tier bump. `specialization: planning-reasoning` breaks ties among candidates already
   meeting the required strength. Then apply the **verifier floor** `tiering.verifier.minTier` (DoR
   §6.5.1): if the resolved rung came out *below* it, raise it — the floor **never selects** a rung
   and **never lowers** a result. The bump and the floor both obey the costly floor: **if the only
   stronger (or only floor-satisfying) block is `costly`, the judge stays put and an advisory
   fires** — it degrades, it never overspends, it never climbs a rung to compensate, and it is
   **not** an error (no GR code: a verifier condition may never fail a build). Apply
   the **judge block's** `guardrailOverrides`, not the actor block's. Record the judge route in the
   attempt's `judge {...}` provenance. Surface at **BOTH boundaries (charter Decision 9, both v1)**:
   a **startup preflight** advisory line over the statically-predicted pairs, **and a per-attempt
   JIT re-check** of the pair the resolver actually returned. The JIT half is not redundant in
   static v1 — the preflight is a *model* of the resolver while the JIT check *is* the resolver
   (the #382 lesson), and it is the only boundary that sees a config edit on resume, an
   overwatcher-applied action change, or a hand-edit between waves. **De-dup:** record
   `judge.advisory` in provenance every attempt, but log a line only when the observed pair differs
   from the preflight's prediction (DoR §6.5).

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
0. **(revision 3)** Flag a **judge guardrail weaker than the actor it verifies**, and the
   **equal-and-weak** case (a local/weak model judging itself — the #382 pattern at the model
   layer). Advisory findings, exactly like the rest of #229; **never** a GR code and never a halt,
   in attended or unattended mode (charter Decisions 1 and 10).
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
- **The costly floor holds where it must and yields where it should:** a `costly: true` block is
  never selected for its own rung, for a stronger-rung climb, or for a judge bump — and IS reached
  by an explicit `action.runner`/`action.model` pin. A config whose only `hard`-capable block is
  `costly` fails validation with **GR2046**, and the message says *which* of the two causes it is.
- **The verifier rule:** a weak actor's judge resolves one strength rank up; a strong actor's judge
  does not move; when the only stronger block is `costly` the judge stays put, an advisory is
  emitted, **and the run proceeds** (it degrades, it never overspends).
- **The verifier floor:** with `tiering.verifier.minTier: "medium"`, an `easy` task's judge resolves
  at `medium`; a judge that already resolved at `hard` is **untouched** (the floor never lowers);
  and a floor that no non-costly block can serve produces an **advisory, not an error**, with the
  judge left at its unfloored result.
- **Invariant 7 extends to the verifier half:** a routing-enabled config + zero-tag plan carrying a
  judge guardrail does **zero** judge-tiering activity — no bump, no preflight line, no `judge`
  provenance, no report line.

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
