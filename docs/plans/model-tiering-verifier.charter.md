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
4. **Both granularities — and the per-plan half is a FLOOR, not a default** (`verifier-default-tier`,
   answered in review). A per-plan **minimum** verifier tier **plus** a per-task/per-judge-guardrail
   override. Per-plan alone fails both ways: too low for a task that graduated to a strong model, too high
   (and too expensive) for the majority that didn't — which is exactly why the plan-wide knob must not
   *choose* the judge's tier. **It never selects; it only refuses a result that came out below it, and it
   never lowers one.** The rule in Decision 2 (the actor's tier, bumped when the actor is weak) remains the
   thing that chooses. The knob is `tiering.verifier.minTier` (DoR §6.5.1). **This is the same floor
   Decision 6 refers to — there is exactly one.**
5. **All four verdict surfaces** obey the rule: per-task prompt-judge guardrails, the terminal
   `<plan>/guardrails/` phase, the autonomous review-gate (#361), and the overwatcher (#269).
6. **Rides up with escalation, floored.** When an actor escalates to a stronger tier on retry (#228), the
   judge re-resolves to stay ≥ it, and never drops below the configured verifier floor. **That floor is
   Decision 4's `tiering.verifier.minTier`** — the two Decisions describe one knob, not two (the floor
   applies in a static run as well; escalation is simply the other thing it bounds).
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

## Reconciliation into the design-of-record

These Decisions are now folded into **[`17-model-tiering.md`](17-model-tiering.md)** (DoR revision
3), which owns the **contracts**; this charter remains the rationale and the review record. Where
the two differ, **the DoR wins** — the same rule the three stage briefs follow. Landing map:

| Charter Decision | Lands in the DoR as |
|---|---|
| 7 — three independent axes | **§4.1** (D21) — `costly`/`strength`/`specialization`, **top-level on the block, not inside `routing`**, because a *reserved or pinned* block has a strength too and the ≥ comparison needs it |
| 8 — generated registry | **§4.3** (D23) — `guardrails providers init`, writing comment-annotated blocks into `guardrails.json` itself (it already parses `//` comments), idempotent. **`providers-init-claude` settled it: it may never invent a model name** — a registry entry is a routing target, so a fabricated id would be spent against at a model that may not exist |
| 3 — never auto-select `costly` | **§6.2** (D22) — one candidacy predicate excludes costly blocks from every rung, every climb, every judge bump, and (v2) every ladder escalation |
| 1, 2, 5, 10 — the judge rule | **§6.5** (D24) — v1 and static; advisory via #229; never blocking |
| **4 + 6 — the verifier FLOOR (one concept, not two)** | **§6.5.1** (D27), **v1** — `verifier-default-tier` settled that the plan-wide knob is a **floor**, not a default: `tiering.verifier.minTier` never selects the judge's rung, it only refuses one that came out below it and never lowers a result. Decision 6's "never drops below a configured verifier floor" **is this same knob**. Unsatisfiable without a costly block ⇒ **degrades to the advisory** (Decision 3 admits no exception), never an error, never a rung climb |
| 9 — surfaced at BOTH boundaries | **§6.5**, **both v1** — startup preflight AND per-attempt JIT re-check. An earlier DoR draft proposed deferring the JIT half (nothing graduates in static v1); **the maintainer overruled that on 2026-08-12** and the DoR now states what the second boundary buys without graduation: the preflight is a *model* of the resolver, the JIT check *is* the resolver, and it is the only boundary that sees a mid-run config edit, resume, or overwatcher change |
| 6's escalation half only | **v2 with the #228 ladder** (DoR §7) — re-resolving the judge *upward as the actor graduates*. The floor itself is v1 (row above): the judge's tier varies across tasks in a static run, so a floor is reachable without any graduation |

**Two corrections the DoR had to make, recorded here so this charter is not read as still saying
otherwise:**

1. **The bump is in STRENGTH, never in tier** (DoR §6.5 / D24a). Decision 2 says *"bumped one tier
   ABOVE"* and the diagram says *"one strength rank ABOVE"*. Those are different operations, and
   only the second is coherent: **tier** describes how hard the *work* is, **strength** describes
   how capable a *model* is, and bumping the tier would mean "pretend the work is harder".
2. **The provider-kind fallback is verifier-only — RATIFIED 2026-08-12** (DoR §4.1 / D21a).
   Decision 7's *"local-inference ⇒ weak, cloud frontier ⇒ not weak"* cannot key on the DoR's `kind`
   enum as written, because **`openai-compat` covers both** a loopback Ollama endpoint and a cloud
   OpenAI-compatible API. The fallback therefore reads `kind != "claude"` ⇒ weak-unless-declared and
   is used **only** for the judge comparison. The asymmetry is the point: the guess is allowed
   exactly where being wrong costs **one spare advisory** on an already-advisory rule, and forbidden
   exactly where it would **misroute real spend**.

**Settled at the same time (2026-08-12), so they are not re-opened:**

- **Disposition 2's flagged consequence is CONFIRMED, with its counterpart stated:** when "judge ≥
  actor" cannot be met without a costly model, the verifier rule **degrades to an advisory and the
  run proceeds**. The actor route does the **opposite** — it **halts** (GR2046 at validate time,
  `no-route` at runtime), because a judge is advisory-and-never-alone by construction while an actor
  route is load-bearing. **Degrade what is advisory; halt what is load-bearing.** Neither overspends.
- **Decision 9 stands as BOTH, and both halves are v1.** The DoR briefly proposed deferring the JIT
  re-check on the grounds that nothing graduates in a static v1; **overruled.** See the Decision-9
  row in the table above for what the second boundary buys before graduation exists.
- **`costly` means "never automatic, full stop."** A `costly: true` block is never auto-selected at
  any rung — not by the resolver, not by the judge bump, not by the future escalation ladder. If
  that leaves a difficulty tier with no eligible model, `guardrails validate` **fails** (GR2046)
  rather than routing around it, and the axis is **not** split into a costly-for-accounting flag
  plus a separate reserved-for-the-floor flag. This is the review answer to `bump-ceiling` taken
  literally: *"They should never be chosen by the harness, only the user can specify to use them
  for a task."*

## Proposed shape

- Extend the **provider registry (#224)** with the three axes of Decision 7 (`costly`, `strength`,
  `specialization`, each admitting `unspecified`), and add the `.jsonc` generation of Decision 8 —
  enumerate the provider's models, emit them annotated with the legal enum values in comments.
- Extend the tier resolution (#226) so a prompt-**judge** guardrail resolves its own `(provider, model,
  effort)` — chosen per Decision 2, raised (never lowered) by Decision 4's `tiering.verifier.minTier`
  floor, and hard-bounded by Decision 3 (never auto-select `costly`, not even to satisfy the floor —
  an unsatisfiable floor degrades to the advisory).
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

## Decisions taken in review (answered inline)

All five original questions are settled and folded into **Decisions** above; the blocks below are the
durable record of what was asked and what was answered. Disposition 2's flagged *consequence* has since been
**confirmed** (the rule degrades, it never overspends). **Three NEW open questions** — raised by reconciling
these Decisions into the design-of-record — were raised, answered, and are recorded in *Open decisions* below.
**All eight questions in this charter now carry answers; nothing is open.**

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

## Open decisions — ALL ANSWERED (2026-08-12)

**Nothing is open.** These three questions came out of folding this charter's Decisions into the
design-of-record ([`17-model-tiering.md`](17-model-tiering.md) §11); all three are now answered, and the
blocks below are kept as the durable record of what was asked and what was chosen — the same treatment as
the five original questions above. What each answer did:

| Question | Answer | Effect on the design |
|---|---|---|
| `providers-init-claude` | Degrade honestly; never invent a model name | **Ratified** the designed behavior, and promoted "never invent a model name" to a hard rule with its reason: a registry entry is a **routing target**, so a fabricated id would be spent against at a model that may not exist (DoR §4.3) |
| `retire-rank` | Drop `routing.rank`; order by ascending `strength` | **Ratified** D25. *The weakest model that can serve the tier goes first* — a cost-minimising default the deterministic gate makes safe. "This model should not serve that tier" is now said by editing its `routing.tiers`. A leftover `rank` key raises warning **GR2054** so ordering never changes silently (DoR §4.2) |
| `verifier-default-tier` | Keep it, but as a plan-wide **FLOOR** | **CHANGED the design.** `tiering.verifier.minTier` never *selects* the judge's rung — the Decision-2 rule still does — it only refuses a result that came out below it, and never lowers one. Collapses Decisions 4 and 6 into **one** floor concept, moves the floor from v2 into **v1**, and **degrades to the advisory** rather than reaching a costly model or climbing a rung when unsatisfiable (DoR §6.5.1) |

---

**1. Can Guardrails actually list Claude's models?** *(binds this charter — Decision 8, the generated
registry.)*

Decision 8 says Guardrails enumerates each provider's models and writes them into a comment-annotated
config for you to annotate with `costly` / `strength` / `specialization`. For a local or
OpenAI-compatible endpoint that is a real API call (`GET /v1/models`) and it works. **For the Claude CLI
there may be no stable, supported way to list models** — the same wall the usage-probe question hit.

The design currently **degrades honestly**: for a provider it cannot enumerate, `guardrails providers
init` annotates the blocks *already in your config* with the legal values in comments, adds a "could not
enumerate models for kind 'claude' — add blocks manually" note, and **never invents a model name**. So
you still get the annotated form you asked for; you just add the Claude blocks yourself the first time.
The alternative would be shipping a curated model list inside Guardrails, which goes stale the week
after a release and would quietly point you at a retired model.

:::question
{"id":"providers-init-claude","title":"`guardrails providers init` may be unable to enumerate the Claude CLI's models. What should it do for that provider?","mode":"single","options":["Degrade honestly — annotate the blocks already in the config with the legal axis values, add a 'could not enumerate' note, and never invent a model name (the current design)","Ship a curated model list inside Guardrails for kinds that cannot be enumerated, accepting that it goes stale between releases","Fail the command for a provider it cannot enumerate, so the gap is impossible to miss"],"target":"human", "answer": ["Degrade honestly \u2014 annotate the blocks already in the config with the legal axis values, add a \u0027could not enumerate\u0027 note, and never invent a model name (the current design)"]}
:::

→ **Answered: degrade honestly.** Now a hard rule — a model id may only come from a provider that
reported it or a human who typed it (DoR §4.3 ruling 2).

---

**2. Two ways to order models, or one?** *(binds the design-of-record — the ACTOR-side routing, not this
charter's judge rule. Called OD-F there.)*

Decision 7 gave every registry model a **`strength`** rank (higher = stronger) so the judge bump has
something to compare. The actor-side design already had a separate **`routing.rank`** (lower = wins) for
choosing between two models that both serve a difficulty tier. That leaves two orderings on the same
blocks pointing opposite ways — a reliable source of backwards-comparison bugs.

The design currently **drops `routing.rank`** and orders candidates by **ascending `strength`** — the
weakest model you said may serve this tier goes first, which is the whole point of tiering. To say
"sonnet should not serve hard work", you remove `hard` from sonnet's tier list, which is the honest
place to say it. **If nobody annotates `strength` at all, ordering is declaration order — identical to
today.** Keeping both is possible; it just means maintaining a preference list by hand *and* living with
two opposite polarities.

:::question
{"id":"retire-rank","title":"A model now has a `strength` rank (higher = stronger). Should the separate `routing.rank` preference field (lower = wins) be dropped?","mode":"single","options":["Drop `routing.rank` — order candidates by ascending `strength` (weakest model that can serve the tier goes first); express 'this model should not serve that tier' by editing its tier list","Keep `routing.rank` as an optional explicit override that wins over strength-ordering when present, accepting two ordering fields with opposite polarity","Keep `routing.rank` as the only ordering field, and use `strength` solely for the judge-vs-actor comparison"],"target":"human", "answer": ["Drop \u0060routing.rank\u0060 \u2014 order candidates by ascending \u0060strength\u0060 (weakest model that can serve the tier goes first); express \u0027this model should not serve that tier\u0027 by editing its tier list"]}
:::

→ **Answered: drop `routing.rank`.** Candidates order by ascending `strength`; a leftover `rank` key
raises warning GR2054 so a migrated config's ordering never changes silently (DoR §4.2).

---

**3. Do you want a plan-wide setting for the judge's tier?** *(binds this charter — Decision 4, "both
granularities".)*

Decision 4 asked for a per-plan default verifier tier **plus** a per-judge override. The per-judge
override exists (a `tier` in the judge guardrail's frontmatter). The per-plan default is currently a
`tiering.verifier.defaultTier` key — but the judge tier is **already chosen automatically** by
Decision 2's rule (judge = the actor's tier, bumped one strength rank when the actor is weak), so the
plan-wide key only exists to override that rule for a whole plan at once. It may be a knob nobody ever
turns, and it is the cheapest thing in the design to remove.

:::question
{"id":"verifier-default-tier","title":"The judge's tier is already chosen automatically (actor's tier, bumped when the actor is weak). Is a plan-wide `tiering.verifier.defaultTier` override still wanted?","mode":"single","options":["Yes — keep the plan-wide key as Decision 4 asked, as an escape hatch when the automatic rule is wrong for a whole plan","No — drop it; the automatic rule plus the per-judge frontmatter override covers every real case, and an unused knob is a cost","Keep it, but only as a plan-wide FLOOR (never below tier X) rather than a plan-wide default"],"target":"human", "answer": ["Keep it, but only as a plan-wide FLOOR (never below tier X) rather than a plan-wide default"]}
:::

→ **Answered: keep it as a plan-wide FLOOR.** It is now `tiering.verifier.minTier` — it never selects
the judge's rung, only refuses one that came out too low. **The lead-in above describes the *default*
semantics this answer replaced**; the floor rules are DoR §6.5.1.

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
