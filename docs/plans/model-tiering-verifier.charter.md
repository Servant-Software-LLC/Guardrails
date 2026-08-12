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
3. **The harness NEVER auto-selects a costly model — only the user may assign one.** Settled in review
   (`bump-ceiling`), and stronger than any of the three options offered: this is not a ceiling the bump
   negotiates, it is a hard floor on harness autonomy. A model carrying `costly: true` is reachable only
   by explicit user assignment to a task. "Actor is Opus, so we judge with Fable" is exactly the consumer
   pushback this forecloses.
   **Derived consequence — the rule degrades, it never overspends.** When "judge ≥ actor" cannot be
   satisfied without a costly model (the actor IS costly, or graduated there under #228), the harness does
   **not** auto-select one. It emits the #229 advisory and proceeds — which is precisely Decision 1 and the
   `autonomous-advisory` answer working as intended. An unsatisfiable rule surfaces; it never spends.
4. **Both granularities.** A per-plan default verifier tier **plus** a per-task/per-judge-guardrail
   override. Per-plan alone fails both ways: too low for a task that graduated to a strong model, too high
   (and too expensive) for the majority that didn't.
5. **All four verdict surfaces** obey the rule: per-task prompt-judge guardrails, the terminal
   `<plan>/guardrails/` phase, the autonomous review-gate (#361), and the overwatcher (#269).
6. **Rides up with escalation, floored.** When an actor escalates to a stronger tier on retry (#228), the
   judge re-resolves to stay ≥ it, and never drops below a configured verifier floor.
7. **A registry entry carries THREE INDEPENDENT axes, not one blended tier** (`weak-detection`). The
   review's own words: *"maybe that makes for too many values in one option. Let's divide it out."* So
   each provider-registry model (#224) declares:
   - **`costly`** — boolean. Drives Decision 3 and nothing else.
   - **`strength`** — the rank, and the ONLY axis the "judge ≥ actor" comparison and the one-tier bump
     read. Comparison needs a total order; only this axis has one.
   - **`specialization`** — a **small fixed enum: `coding` / `planning-reasoning` / `general` /
     `unspecified`** (`specialization-values`). A **preference, never an ordering** — it cannot satisfy or
     violate ≥, and a mismatch is not a rule breach. Judge selection **does** read it: among candidates
     that already meet the required strength, prefer `planning-reasoning`. Strength gates; specialization
     only breaks ties. A fixed enum rather than a free-form string precisely so the harness can act on it.

   **Every axis admits `unspecified`**, and `unspecified` strength is what makes the answer "Both" rather
   than "rank only": the ≥ rule falls back to **provider kind** (local-inference ⇒ weak; cloud frontier ⇒
   not weak) exactly when rank is absent. A registry with nothing declared still behaves correctly.
8. **The registry is generated, not hand-typed from memory** (`weak-detection`). Guardrails enumerates each
   configured provider's models and emits a **`.jsonc`** config — comments carrying the allowed enum values
   per axis — for the user to annotate. Comment-bearing JSON is the point: the legal values are discoverable
   in the file being edited, not only in the docs.
9. **Surfaced at BOTH boundaries** (`check-timing`): a **startup preflight** over what is statically known,
   **plus a JIT re-check each attempt**. The preflight catches a misconfigured plan before any spend; the
   JIT re-check is the only thing that can see a tier the actor reached by graduating mid-run.
10. **Unattended mode behaves identically to attended** (`autonomous-advisory`): advisory only, the finding
    lands in the run report. A weak judge does not halt an autonomous run — consistent with Decision 1, and
    with the harness never blocking on a model-quality opinion.

## Proposed shape

- Extend the **provider registry (#224)** with the three axes of Decision 7 (`costly`, `strength`,
  `specialization`, each admitting `unspecified`), and add the `.jsonc` generation of Decision 8 —
  enumerate the provider's models, emit them annotated with the legal enum values in comments.
- Extend the tier resolution (#226) so a prompt-**judge** guardrail resolves its own `(provider, model,
  effort)` — defaulting per Decision 2, and hard-bounded by Decision 3 (never auto-select `costly`).
- Resolve the judge **per attempt, alongside the actor** (Decision 6): because the actor's tier only
  settles at attempt-launch and can graduate on retry, a judge fixed at plan-load is stale by definition.
- Add the **startup preflight** of Decision 9 over the statically-known tiers, so a plan whose configured
  judge is already too weak is reported before the run spends anything. The per-attempt re-check stays —
  the two are complements, not alternatives, and neither subsumes the other.
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
  R -->|"actor is local / weak<br/>(rank, or provider kind when unspecified)"| UP["Judge = one strength rank ABOVE"]
  UP --> CAP{"Is that judge<br/>costly: true?"}
  CAP -->|"no"| V["Verdict may vouch"]
  CAP -->|"yes — the harness NEVER<br/>auto-selects a costly model"| F
  EQ --> V
  V -.->|"judge &lt; actor, or equal-and-weak"| F["#229 advisory finding<br/>(run proceeds)"]
:::

## Review notes — disposition

Your three inline notes (2026-08-09) are answered below. Charter's CLI has no agent-reply path, so the
dispositions live here rather than as replies in the review pane.

**1. "If the judge is equal and is not a frontier model, then a warning… Qwen judging Qwen should warn,
Opus judging Opus should not. Maybe we can only detect local vs cloud inference?"**
→ Settled, and **your fallback guess became the rule**. The equal-but-weak case is called out in
*The principle* and drives Decision 2's bump. Detection is Decision 7: `strength` rank when declared, and
**exactly your suggestion — provider kind (local vs cloud) — whenever rank is `unspecified`**. So it works
with a registry the user never annotates, and gets sharper when they do.

**2. "The runner may end on Opus (because of graduation) but the judge may be Sonnet 5. Should we consider
auto-elevating the judge?"**
→ Yes — Decision 6 re-resolves the judge upward on every escalation. **But the naive answer is now wrong,
and this is the sharpest interaction in the plan:** your own `bump-ceiling` answer forbids the harness from
auto-selecting a costly model. If Opus carries `costly: true`, the judge **cannot** be auto-elevated to
meet it. The rule then *degrades rather than overspends* — it emits the #229 advisory and the run proceeds
(Decision 3's derived consequence). Worth confirming that is what you intended: the alternative reading is
that graduation should be allowed to drag the judge up with it, costly or not.

**3. "Is this surfaced at harness startup or JIT? Since a task runner can graduate with re-attempts, it
matters. Right?"**
→ Right, and it settled the `check-timing` question: **both** (Decision 9). Your reasoning is exactly why
startup alone is insufficient — a tier reached by graduating mid-run is invisible to any preflight — while
the preflight still earns its place by catching a misconfigured plan before a single token is spent.

## Decisions taken in review (answered inline)

All five questions are settled and folded into **Decisions** above; the blocks below are the durable record
of what was asked and what was answered. No open question blocks remain — though **disposition 2** flags one
*consequence* of the settled rules worth confirming before implementation, since the naive reading of that
note is now wrong.

:::question
{"id":"weak-detection","title":"How do we decide a model is \"local / weak\" and therefore needs the one-tier bump?","mode":"single","options":["Provider kind — local-inference endpoints are weak, cloud frontier APIs are not (no per-model judgement)","An explicit strength/rank field on each provider-registry entry (#224), authored by the user","Both — rank when declared, fall back to provider kind when it isn't"],"target":"human", "answer": ["Both.  Guardrails knows the model providers and should be able to enumerate its models and provide them for configuration if requested in a jsonc file which will have comment to indicate the enum values allowed to attach to a model.  One of those enum values should be unspecified.  The range of values needs to be enough to cover not only strength but even some specialization.  (like a coding vs. planning models and/or high thinking and costly, etc).  Hmmm.. maybe that makes for too many values in one option.  Let\u0027s divide it out.  Have a boolean value for costly and another value for specialization and another which ranks their strength, so that bumping up can occur."]}
:::

:::question
{"id":"bump-ceiling","title":"What bounds an AUTOMATIC judge bump, so it never silently reaches a costly model?","mode":"single","options":["Never auto-select a model not already declared in the plan's provider registry","Allow any registered model, but require an explicit opt-in flag for cost-tier models","A configured \"max auto judge tier\" the bump may never exceed"],"target":"human", "answer": ["The costly flag from the previous question answer addresses which models are considered costly.  They should never be chosen by the harness, only the user can specify to use them for a task."]}
:::

:::question
{"id":"check-timing","title":"When is the judge-vs-actor mismatch surfaced?","mode":"single","options":["JIT only — per attempt, since the actor's tier only settles at attempt-launch and can graduate","Startup only — a preflight over the plan's configured tiers","Both — a startup preflight on what's statically known, plus a JIT re-check each attempt"],"target":"human", "answer": ["Both \u2014 a startup preflight on what\u0027s statically known, plus a JIT re-check each attempt"]}
:::

:::question
{"id":"autonomous-advisory","title":"Advisory means the run proceeds — but in UNATTENDED autonomous mode (#361/#269), nobody reads a warning mid-run. What happens there?","mode":"single","options":["Same as attended — advisory only; the finding lands in the run report","Escalate to a halt under the autonomy policy (a weak judge is a review-integrity problem)","Configurable via the existing unified autonomy policy (prompt / halt / auto)"],"target":"human", "answer": ["Same as attended \u2014 advisory only; the finding lands in the run report"]}
:::

:::question
{"id":"specialization-values","title":"Decision 7 gives `specialization` its own axis but does not enumerate its values — what are they, and does judge selection read them?","mode":"single","options":["A small fixed enum (coding / planning-reasoning / general / unspecified), and judging PREFERS planning-reasoning at the required strength when one is available","The same small fixed enum, but recorded for reporting and user routing only — judge selection ignores specialization entirely","A free-form user-defined string with no harness semantics beyond display and manual routing"],"target":"human", "answer": ["A small fixed enum (coding / planning-reasoning / general / unspecified), and judging PREFERS planning-reasoning at the required strength when one is available"]}
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
