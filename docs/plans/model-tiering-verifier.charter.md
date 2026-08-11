---
charter-format-version: 1
---
# Verifier/judge model-tiering — the "judge ≥ actor" rule (Guardrails #201)

The model-tiering epic (#201) tiers the **actor** — the model that *does* a task's work — across a
provider registry (#224), difficulty tags (#225), per-attempt resolution (#226), an escalation ladder
(#228), and steering (#231). Local inference (e.g. a Qwen-32B endpoint) is a first-class actor tier.

This plan adds the missing half: a dial for the **verifier** — the model that *judges* whether the work
passed — and a rule relating the verifier's tier to the actor's. Filed as an addition to the epic; the
design-of-record draft (#342) is still open, so this folds in rather than retrofits.

:::note
**Most Guardrails verification is model-agnostic and untouched by this.** Deterministic guardrails —
tests, exit codes, regex, build/compile checks — are the strongest gate and don't run a model at all. This
plan governs ONLY the layer where an LLM renders the verdict: the **prompt-judge guardrails**
(the demoted, never-alone secondary), the terminal `<plan>/guardrails/` phase when it carries a judge, and
the autonomous review surfaces. Nothing here weakens the deterministic-first posture; it hardens the one
place a model's opinion is load-bearing.
:::

:::warn
**The failure this prevents: a weak actor grading its own homework.** If tasks run on a local Qwen-32B and
its *judge* guardrails also run on Qwen-32B (or weaker), a plausible-but-wrong implementation and a
plausible-but-wrong "looks good to me" can agree — the run goes green over broken work, exactly the
"passing-but-blind" pattern (#382) but at the model layer. A stronger (or equal) judge is the structural
guard.
:::

## The principle

**A prompt may propose, only an equal-or-stronger judge may vouch.** It mirrors the deterministic-gate
ethos one level up: where a *model* must issue the verdict, that model's capability tier must be ≥ the tier
that produced the work.

**Equal is not always enough.** The review sharpened this: two *frontier* peers checking each other is a
real check, but two instances of the same *weak* model are one blind spot talking to itself. Opus judging
Opus needs no warning; Qwen judging Qwen does. So the rule is ≥ **plus** a weak-tier bump — see Decision 2.

## What already exists (the actor side)

- **#224** provider registry (`kind`-switched runners — a local endpoint slots in beside Claude/OpenRouter).
- **#225** plan-breakdown difficulty-tier tags per task.
- **#226** per-attempt `tier → (provider, model, effort)` resolution (re-resolved every retry).
- **#228** escalation ladder (a task that fails its guardrails graduates to a stronger tier next attempt).
- **#229** guardrails-review flags an *action* tier that's wrong for the task's difficulty.
- **#231** interactive/ambient steering ("lean hard on local inference").

The `promptRunners.<name>.guardrailOverrides` seam already gives verdict prompts a *distinct profile*
(permissions/tools/turns) — but it does **not** carry a model/tier, and nothing relates the judge's tier
to the actor's.

## The gap (the verifier side)

1. **No dial** selects the judge's provider/model/tier independently of the actor's.
2. **No rule** relates the judge's tier to the actor's.
3. **guardrails-review** checks the actor tier fits the task (#229) but never checks the judge is strong
   enough to grade it.

## Decisions (settled in review)

1. **Advisory, not blocking.** A judge weaker than its actor is surfaced by extending the
   guardrails-review **#229** check; the run proceeds. No new hard-error GR code, no load-time refusal.
2. **Default judge tier = the actor's resolved tier, bumped one tier ABOVE when the actor is
   local-inference or a weak model.** Equal is the floor, not the universal default.
3. **Never silently spend.** An automatic bump must not reach for a costly model the user has not
   explicitly declared — "actor is Opus, so we judge with Fable" is exactly the consumer pushback to
   avoid. Auto-selection is bounded (see the open question on the ceiling).
4. **Both granularities.** A per-plan default verifier tier **plus** a per-task/per-judge-guardrail
   override. Per-plan alone fails both ways: too low for a task that graduated to a strong model, too high
   (and too expensive) for the majority that didn't.
5. **All four verdict surfaces** obey the rule: per-task prompt-judge guardrails, the terminal
   `<plan>/guardrails/` phase, the autonomous review-gate (#361), and the overwatcher (#269).
6. **Rides up with escalation, floored.** When an actor escalates to a stronger tier on retry (#228), the
   judge re-resolves to stay ≥ it, and never drops below a configured verifier floor.

## Proposed shape

- Extend the tier resolution (#226) so a prompt-**judge** guardrail resolves its own `(provider, model,
  effort)` — defaulting per Decision 2, bounded per Decision 3.
- Resolve the judge **per attempt, alongside the actor** (Decision 6): because the actor's tier only
  settles at attempt-launch and can graduate on retry, a judge fixed at plan-load is stale by definition.
- Extend **guardrails-review (#229)** to surface a judge weaker than the actor it verifies (Decision 1),
  and to surface the equal-but-weak case (two instances of the same local/weak model).
- Record the judge's resolved tier in the attempt log alongside the actor's (extends #198/#230), so the
  cost split shows what verification actually cost.

:::diagram
flowchart TB
  A["Task action<br/>(actor tier resolved per attempt)"] --> W["Work produced"]
  D["Deterministic guardrails<br/>(model-agnostic — unaffected)"] --> W
  W --> R{"Resolve judge tier<br/>(same attempt)"}
  R -->|"actor is frontier/strong"| EQ["Judge = actor's tier<br/>(equal is sufficient)"]
  R -->|"actor is local / weak"| UP["Judge = one tier ABOVE"]
  UP --> CAP{"Cost-consent ceiling<br/>(no undeclared costly model)"}
  EQ --> V["Verdict may vouch"]
  CAP --> V
  V -.->|"judge &lt; actor, or equal-and-weak"| F["#229 advisory finding<br/>(run proceeds)"]
:::

## Open decisions (for your review)

:::question
{"id":"weak-detection","title":"How do we decide a model is \"local / weak\" and therefore needs the one-tier bump?","mode":"single","options":["Provider kind — local-inference endpoints are weak, cloud frontier APIs are not (no per-model judgement)","An explicit strength/rank field on each provider-registry entry (#224), authored by the user","Both — rank when declared, fall back to provider kind when it isn't"],"target":"human"}
:::

:::question
{"id":"bump-ceiling","title":"What bounds an AUTOMATIC judge bump, so it never silently reaches a costly model?","mode":"single","options":["Never auto-select a model not already declared in the plan's provider registry","Allow any registered model, but require an explicit opt-in flag for cost-tier models","A configured \"max auto judge tier\" the bump may never exceed"],"target":"human"}
:::

:::question
{"id":"check-timing","title":"When is the judge-vs-actor mismatch surfaced?","mode":"single","options":["JIT only — per attempt, since the actor's tier only settles at attempt-launch and can graduate","Startup only — a preflight over the plan's configured tiers","Both — a startup preflight on what's statically known, plus a JIT re-check each attempt"],"target":"human"}
:::

:::question
{"id":"autonomous-advisory","title":"Advisory means the run proceeds — but in UNATTENDED autonomous mode (#361/#269), nobody reads a warning mid-run. What happens there?","mode":"single","options":["Same as attended — advisory only; the finding lands in the run report","Escalate to a halt under the autonomy policy (a weak judge is a review-integrity problem)","Configurable via the existing unified autonomy policy (prompt / halt / auto)"],"target":"human"}
:::

## Scope / non-goals

- **In:** the model/tier of LLM *verdict* prompts, the default/bump rule relating judge to actor, and the
  advisory surfacing of a violation.
- **Out:** deterministic guardrails (unchanged, model-agnostic); the actor-side tiering (#224–#231, already
  designed); any change to the "deterministic-first, judges-never-alone" catalogue posture; any hard
  load-time refusal (explicitly decided against).

## Related

#201 (epic) · #224–#231 (model-tiering waves) · #229 (review appropriateness — the carrier for the
advisory) · #228 (escalation, which the judge now rides) · #269 (overwatcher) · #361 (autonomous
review-gate) · #382 (passing-but-blind, the model-layer analogue) · DoR draft #342.
