---
charter-format-version: 1
---

# Escalation ladder: a task's retry graduates to a stronger tier (#228)

A task starts its first attempt at its tier's resolved provider/model. When an attempt fails its
guardrails, the next attempt resolves **one rung stronger** — turning model choice into a
self-correcting retry policy rather than a static, one-shot assignment.

The point is that **no upfront difficulty judgment has to be correct** for the system to work. The
retry loop discovers empirically that a task needed a stronger model.

## Why now

Two things changed since #228 was filed in July.

**It is the only backstop that is not a human opinion.** `/guardrails-review`'s model-appropriateness
probe says so about itself, in its own text: *"with the runtime ladder (#228) deferred there is no
backstop and a mis-tag is caught here or not at all."* Today a mis-tagged task is caught by a review
pass reading tags by eye, or it is not caught. That is the arrangement the harness exists to replace.

**Its dependencies shipped.** #224 (provider registry) and #226 (tier → concrete provider/model/effort)
are both closed. `TierResolver` is live and now drives the dry-run preview as well (#549). Nothing
blocks this.

And a Mac Studio arrives 2026-09-22 (#570). "Start local, escalate to frontier when the guardrails say
the work needed more" is what makes a local-first tier viable without the tags being right.

## What already exists, measured

These are facts about the tree as it stands, not assumptions:

- `ActionTiers.All` is `[easy, medium, hard]`, **ordered ascending by difficulty**, and documented as
  such.
- `TierResolver.SelectCandidate(config, tier)` already walks *rungs at or above* the requested one and
  returns a `TierResolution` (or `NoRoute`). **Escalation is therefore calling the existing resolver
  with a higher rung** — not a new resolution algorithm.
- The retry budget is `int budget = 1 + (task.Retries ?? _plan.Config.DefaultRetries)`, default `2`
  retries, so **3 attempts** by default.
- `TaskExecutor` already distinguishes failure kinds: `timeoutRetries` and `maxTurnsRetries` are
  counted separately, and an inner pause loop re-runs the *same* attempt across transient pauses
  **without consuming the retry budget**.
- There is already precedent for extending a budget under a hard ceiling: granted retries are capped by
  `MaxCumulativeGrantedRetries` so repeated grants can never grow the budget without limit.
- `feedbackPath` — the composed `feedback.md` explaining why the last attempt failed — is passed to the
  next attempt **independently of which model runs it**.

:::note
That last fact is the one that matters most for the decision below, and it was not obvious before
reading the loop. Escalating does **not** trade away the retry-feedback mechanism the codebase has
invested heavily in (#179, and #608's re-emit repair). An escalated attempt gets a stronger model
**and** the feedback. The two are independent.
:::

## The mechanism

1. An attempt fails its guardrails.
2. The next attempt's tier is the next rung **up** from the rung the failed attempt actually ran on.
3. `SelectCandidate` resolves that rung as it does today. If the rung no-routes, keep climbing; if no
   rung at or above routes, stay where you are rather than inventing a route.
4. The ladder is capped at the strongest **registered** rung — never escalate past what the config can
   actually serve.
5. If the strongest available rung still fails, the task follows the existing needs-human path exactly
   as today. Escalation never converts an honest halt into a silent pass.

:::warn
**Escalation is triggered by `guardrail-failed` only** — not by a timeout, a max-turns stop, a
transient pause, or a permission wall. A guardrail failure is evidence the model produced *wrong work*.
A timeout is evidence it produced *slow work*, and the harness already has separate counters and a
separate remedy for that. Escalating on a timeout would spend a frontier model on an infrastructure
problem and, worse, would read in the report as "this task was too hard" when nothing of the sort was
measured.
:::

## The decision the epic deferred

#201 left one question explicitly open and handed it to this issue: **does an escalated attempt reset
the retry budget, or consume from the same pool as a same-tier retry?**

With the default budget of 3 attempts and 3 rungs, the options diverge sharply.

:::comparison
| | A — share the pool | B — one same-rung retry first | C — reset per rung, cumulative cap |
|---|---|---|---|
| easy-tagged task, defaults | easy → medium → hard | easy → easy+feedback → medium | 3 easy, 3 medium, 3 hard (capped) |
| Reaches the top rung? | Yes, by attempt 3 | **No** — never reaches `hard` | Yes |
| Worst-case attempts | 3 (unchanged) | 3 (unchanged) | up to 9, needs a new cap |
| Time to an honest halt | unchanged | unchanged | up to 3x longer |
| Spends a strong model on a fixable slip? | Sometimes | Rarely | Rarely |
| New config surface | none | none | a cumulative cap |
:::

The case against A used to be that it discards the same-rung retry the feedback loop was built for.
**That case is weaker than it looks**, because the escalated attempt still receives the feedback. What
A actually costs is spending a stronger model on a mistake a weaker one might have fixed — and cost is
explicitly not the binding constraint here, while a mis-tag reaching a human is.

The case against C is that it triples the time to an honest halt on an unattended overnight run, and it
introduces a second budget concept for a benefit A already delivers within the existing one.

:::question
{"id": "escalation-budget", "title": "Does an escalated attempt reset the retry budget, or share the existing pool?", "mode": "single", "options": ["A - share the existing pool: each guardrail failure climbs one rung, total attempts unchanged", "B - one same-rung retry with feedback first, then climb", "C - reset the budget at each rung, under a new cumulative cap"], "recommended": "A - share the existing pool: each guardrail failure climbs one rung, total attempts unchanged", "rationale": "A reaches the strongest rung by attempt 3 with today's defaults, adds no new budget concept, and does not change worst-case run time or time-to-honest-halt. The usual objection - that it throws away the same-rung retry the feedback mechanism exists for - does not hold here, because feedbackPath is passed to the next attempt regardless of which model runs it, so an escalated attempt is both stronger AND better informed. B is the conservative choice but with default retries it never reaches 'hard' from an 'easy' tag, which defeats the mis-tag backstop this issue exists to be. C gives every rung a fair trial at the price of up to 9 attempts, a second budget concept, and roughly 3x the time before an unattended run reaches a human.", "target": "human", "answer": ["A - share the existing pool: each guardrail failure climbs one rung, total attempts unchanged"]}
:::

## Visibility

Escalation is worthless if a human cannot see it happened. Per the issue: a reviewer should be able to
read *"task X escalated from easy to hard on attempt 3"*.

- The attempt already records its resolved route (`model`, `runner`, `tier`, `tierSource`), so the
  per-attempt record needs no new field to show *where* an attempt ran.
- What is missing is **why** it ran there: an attempt that ran on `hard` because it was tagged `hard`
  and one that ran on `hard` because it climbed are materially different facts, and today they would be
  indistinguishable. This wants a `tierSource` value meaning *escalated*, alongside the existing
  task/plan-default/override origins.
- The telemetry corpus inherits that for free and can then answer the question this feature is really
  betting on: *how often does climbing a rung actually rescue a task?* That is the measurement that
  decides whether the ladder earns its keep, and it is exactly what the corpus became able to answer
  when `modelAttribution` landed (#577).

:::note
Deliberately **not** in scope: escalating a prompt-JUDGE guardrail's own tier. A judge's rung follows
the actor it guards, and a judge that escalates on disagreeing with itself is a different and much
worse idea. Actions only.
:::

## A second, unrelated decision — #298

Folded into this review round rather than spending a second one, because it is one fork and the
context is cheap.

#298 asks `/plan-breakdown` to identify tasks whose uncertainty is best resolved by **research now**,
before review and before any run — converting a latent mid-run `needsHuman` into a pinned action
prompt. It offers two strengths, and the difference is the whole value.

:::question
{"id": "research-strength", "title": "#298: should the breakdown RECOMMEND research, or actually perform it?", "mode": "single", "options": ["Recommend only - list the questions worth resolving before review", "Perform it where cheap and deterministic, recommend the rest", "Perform it for every flagged question"], "recommended": "Perform it where cheap and deterministic, recommend the rest", "rationale": "The issue offers both and the second is the valuable half - a list of open questions is close to what the breakdown report already produces passively, and it moves the work to the human rather than removing it. Performing the cheap deterministic checks (does this tool support this flag, does this symbol exist at this version) and PINNING the answer into the action prompt is what actually converts a mid-run halt into a settled instruction. The third option overreaches: some questions need a judgment or an external decision, and a breakdown that answers those by guessing would pin a wrong answer with the same confidence as a right one - which is worse than flagging it.", "target": "human", "answer": ["Perform it where cheap and deterministic, recommend the rest"]}
:::

## Risks worth naming

- **A mis-tagged-EASY plan gets quietly more expensive.** If many tasks are tagged too low, the ladder
  climbs constantly and the run costs more than the tags suggested. That is the ladder working as
  designed, but it should be visible — the per-run spend-by-tier summary (#230, also queued) is what
  makes it visible, which is an argument for doing the two together.
- **A flaky guardrail becomes an escalation engine.** A guardrail that fails for reasons unrelated to
  model strength (an environment problem, a genuine harness bug) will climb the ladder every time and
  reach `hard` before halting. It still halts honestly, but it will have spent the strongest model
  first. Nothing here can distinguish those cases; the mitigation is that the escalation is recorded,
  so the pattern is legible after the fact.
- **`NoRoute` at a higher rung is not a failure.** A config that declares only one runner has nowhere
  to climb, and the ladder must degrade to today's behaviour silently rather than erroring. Every
  single-runner plan in existence is this case.
