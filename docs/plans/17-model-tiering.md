# 17 — Model tiering, actor + verifier (provider registry + static tier routing; ladder / probes / steering deferred to v2) — design of record (epic #201)

> **Revision 3 (this pass) — what changed and why.** Two forces, neither of which existed when
> revision 2 was written:
>
> 1. **The verifier charter landed and was fully reviewed.**
>    [`model-tiering-verifier.charter.md`](model-tiering-verifier.charter.md) — the "judge ≥ actor"
>    half of #201 — settled 10 numbered Decisions with the maintainer AFTER this DoR was drafted.
>    **Three of them change the registry shape this DoR owns** (three independent model axes; a
>    GENERATED registry; the harness may never auto-select a `costly` model), so they are reconciled
>    here, not bolted on later: §4.1 (axes), §4.3 (generation), §6.2 (the costly floor), §6.5 (the
>    verifier route). The charter remains the rationale/review record; **this DoR owns the
>    contracts.**
> 2. **master moved a month.** Revision 2's assumptions are stale in three places, all corrected
>    here: its reserved diagnostic-code block **GR2037–GR2045 has been entirely taken** by shipped
>    work (#346, #383, #361, #389, #378 — §13 reallocated to **GR2043–GR2054** and explains how the
>    reservation rotted; it has since rotted a THIRD time, at Stage-1 landing, and §13 records that
>    too); this
>    document's number **13 collides** with `13-merge-on-success-default.md` (renumbered **17**); and
>    **#349 is still open**, so §9.3's "additive over #349's base" needed a fallback (§9.3).
>
> The revision-2 skeleton — static v1, the three dynamic subsystems deferred to named v2 bets — is
> **unchanged and reaffirmed**. Nothing below re-opens it.
>
> **Maintainer rulings folded in (2026-08-12), after revision 3's first draft:** (a) charter
> **Decision 9 stands as BOTH and both halves are v1** — the proposed deferral of the per-attempt
> JIT re-check is **overruled**; §6.5 now states what the second boundary buys before graduation
> exists, plus a de-duplication rule. (b) The **verifier-only provider-kind fallback** (D21a) is
> **ratified**. (c) **OD-G is answered — `costly` means "never automatic, full stop"**; the
> unservable-tier consequence is intended and is a validate-time **error**, shown with its exact
> message in §14.1.
>
> **Charter review closed (2026-08-12) — all 8 `:::question` blocks now carry answers.** The last
> three: (d) **`providers init` degrades honestly** — never invents a model name, now a hard rule
> with its reason (§4.3). (e) **`routing.rank` is dropped** — candidates order by ascending
> `strength`, *weakest model that can serve the tier first*, with warning GR2046 on a leftover key
> (§4.2). (f) **the plan-wide verifier knob becomes a FLOOR** — `tiering.verifier.minTier`, which
> **changed the design**: it never selects, only refuses a result that came out too low; it
> collapses charter Decisions 4 and 6 into one floor concept and moves it from v2 into v1 (§6.5.1).
> **Nothing is open in the charter.**

> **Status: DRAFT (revised ×2) — for #106 inline draft-PR review.** This document is the
> contract-locked, build-ready design of record for the model-tiering epic (#201) and its
> sub-issues #223–#231. It **ratifies** the three existing stage briefs —
> `model-tiering-foundation.md` (#224 + #225), `model-tiering-consumers.md` (#226 + #227 + #229
> + #230), `model-tiering-dynamic-behavior.md` (#228 + #231) — as the plan-of-record for scoping
> and acceptance; **this revision re-buckets their issues into a static v1 and a deferred v2
> (§2.2, §10)**, and where this DoR and a stage brief differ, **this DoR wins** (the deltas are
> called out in §2.2). Implementation does not begin until this draft PR's inline review
> comments are addressed (the #106 gate).

> **v1 scope decision (this revision — the organizing decision):** **v1 ships STATIC ROUTING
> only.** It keeps the token-saving core: the provider registry (#224), *gated* difficulty
> tagging (#225), a **pure deterministic tier resolver** (#226-static — effective tier →
> candidate blocks whose `routing.tiers` includes it → order by ascending `strength` → best block → emit
> model/effort; **no probe consultation, no ladder-awareness**), and a **per-tier spend line** in
> the run report (#230-lite). The three *dynamic* subsystems — budget/limit **probes** (#227),
> the **escalation ladder** (#228), and interactive **steering** + `--prefer` (#231) — are
> **deferred to named v2 bets (§10)**, retained in this doc as ratified designs for when v2
> builds them. **Rationale:** they are safety-nets / conveniences for the *mis-tagged minority*,
> not the token-saving mechanism itself; a mis-tagged task simply fails its deterministic gate
> and honestly halts to needs-human for a re-tag (**correctness is never at risk — the gate, not
> the model, certifies**), and #230-lite's measurement is what should decide whether the deferred
> subsystems are ever worth building. Deferring the ladder also removes the Fable devil's-advocate
> pass's BLOCKER (the OD-A last-attempt-at-strongest guarantee) and its worst contradiction (the
> mid-run route-down lever) from the v1 critical path. **Each section head below is tagged
> `[v1]` or `[v2 — deferred]`.**

This document is the SSOT-companion for the contract deltas proposed in §12. Nothing in
`02-schemas-and-contracts.md` is mutated by this change — §12 is a verbatim-appliable proposal
that lands **at build time, stage by stage, in the same change as the code it describes**
(SSOT invariant 4). Where this doc and the live SSOT ever differ after landing, the SSOT wins
for the wire contract; this doc owns the rationale, the rulings, and the phasing.

---

## 1. What it is, and the pain it removes  [v1]

Route each prompt attempt to a difficulty-appropriate (provider, model, effort) instead of
spending frontier tokens on everything. Observed live (preflights dogfood): 4 parallel tasks
burned a usage limit 4% → 29% in ~9 minutes, much of it on routine work (baselines, doc/skill
updates, mechanical migrations) that did not need a frontier model.

**The load-bearing thesis (from #201):** *deterministic guardrails make cheaper models safe.*
A task is certified by its deterministic gate — "a prompt may propose, only a deterministic
gate may certify" — so the model that produced the work matters less: a weaker/local model's
output either passes the gate or it does not. That is what makes tiering low-risk here versus
a bare LLM pipeline where model quality is the only backstop — and it is exactly why v1 can be
**static-only**: when a tag is wrong the gate catches the bad work and the task halts honestly
for a human re-tag, so the escalation ladder (§7) is a *convenience for the mis-tagged
minority*, not a correctness requirement — which is why it is deferred to v2.

**Who decides what, and when** (the #201 "Resolution timing" ruling, 2026-07-04, reaffirmed —
and the answer to review comment 1, "I assume you mean routing during /plan-breakdown"):

| Decision | Who | When | v1? |
|---|---|---|---|
| **difficulty tag** (`easy \| medium \| hard`) | `/plan-breakdown` (#225) or a human hand-edit | **breakdown time** (static) | **v1** |
| **route** = concrete (provider, model, effort) for a tagged task | the harness, deterministically (#226) | **attempt-launch time** | **v1** (static resolver) |
| **explicit pin** (`action.model` / `action.runner` / `action.effort`) | task author | **authoring time** | **v1** (shipped escape hatch) |
| **model axes** (`costly` / `strength` / `specialization` per registry block) | the human, annotating a **generated** registry (§4.3) | **registration time** (out of band, before any run) | **v1** |
| **verifier route** = the (provider, model, effort) that *judges* the work | the harness, deterministically, from the actor's route (§6.5) | **attempt-launch time**, alongside the actor | **v1** (static) |
| **steering** (`--prefer`, mid-run threshold answers) | operator | **mid-run** | **v2 — deferred (#231)** |

One-sentence rationale for resolving the route at *attempt-launch* rather than binding it once
at breakdown: the seam is placed at attempt-launch so the deferred v2 behaviors (ladder, probes,
steering — which legitimately vary the route *between* attempts) can slot in without relocating
the resolver; in **static v1 the resolver is a pure function of (tag + registry)** and therefore
yields the *same* block on every attempt of a task, retries included.

The pieces:

1. The **difficulty tag** (`easy | medium | hard`) is **static** — set by `/plan-breakdown`
   (#225, *gated on tiering being configured* — §5) or a human hand-edit; untagged tasks inherit
   a plan-wide default only if one is set (else legacy resolution — §5).
2. The concrete **(provider, model, effort)** is resolved deterministically by the harness at
   **attempt-launch time** (#226-static, §6) against the current registry (#224). In v1 this is a
   pure static function; the *dynamic* inputs — probe state (#227) and steering (#231) — are v2.
3. `action.model` (shipped, #200), plus `action.runner` and `action.effort`, remain the explicit
   escape hatches that bypass resolution (§6.1).
4. `guardrails-review` (#229) is the pre-run check for missing/mismatched tags — the v1
   tag-quality net that keeps the static story cheap by catching mis-tags *before* the run.
5. **[v2 — deferred]** The **escalation ladder** (#228) would auto-escalate a guardrail-failed
   attempt one rung stronger; deferred because the gate already makes a mis-tag safe (§7, §10).

## 2. Placement, and the three stage briefs

| Slice | Scope | Placement |
|---|---|---|
| Registry (`kind`, `routing`, `effort` on runner blocks) + **the three model axes `costly`/`strength`/`specialization`** + tier fields + validation | **v1** | harness (`Guardrails.Core` loading/validation) + schema (SSOT §2/§3/§4.2 deltas, §12) |
| **Registry generation** — `guardrails providers init` emits/annotates the blocks with the legal axis values in comments (charter D8) | **v1** | CLI (`Guardrails.Cli`) + per-`kind` enumeration in `Guardrails.Core` |
| **The verifier route** — a judge guardrail resolves its own (provider, model, effort) ≥ the actor's, and the weak-actor bump (charter D2/D4/D5/D7) | **v1** (static) | harness (the same `TierResolver`, §6.5) |
| **The costly floor** — the harness never auto-selects a `costly: true` block (charter D3) | **v1** | harness (one candidacy predicate, §6.2) |
| Attempt-launch **static** tier resolution + `no-route` defensive outcome | **v1** | harness (`TaskExecutor` / a new `TierResolver` in `Guardrails.Core.Prompts`) |
| Difficulty tagging doctrine (gated on tiering being configured) | **v1** | skill (`plan-breakdown`) + schema (§3 delta) |
| Model-appropriateness check | **v1** | skill (`guardrails-review`) — advisory findings only |
| Per-tier cost/token spend line in the run report | **v1** (#230-lite) | harness (run-summary aggregation over §9.3 provenance) |
| Provider-unavailability handling (connection failure → shipped #115 pause) | **v1** | harness (§6.3; reuses the shipped `PromptFailureKind` classification) |
| Budget/limit probes + `guardrails providers status` | **v2 (#227)** | harness (`Guardrails.Core` per-kind probe classes) + CLI |
| Escalation ladder + `tierSource: "escalated"` provenance | **v2 (#228)** | harness (attempt loop / the same `TierResolver`) |
| Threshold prompts + ambient steering + `--prefer` | **v2 (#231)** | harness/CLI, governed by the **shared** §2.1 `autonomyPolicy` (new `routing` boundary) — **no new policy field** |
| Concrete non-Claude runner (local OpenAI-compatible endpoint) | **#223, standalone** | plugs into the `kind` seam (§4.4); its internals are NOT designed here |
| Prose/free-text steering interpretation; per-model $ pricing tables; overwatcher tier-pinning | **v2 bets** (§10) | — |

### 2.1 Terminology ruling: "Stage", never "Wave"

The three briefs were written before #254 shipped **waves** as a first-class runtime feature
(nested `<plan>/<wave>/<tasks>`, SSOT §14). Their "Wave 1/2/3" meant *sequential design
phases* — a fatal ambiguity now. **Ruling (D1):** the phases are renamed **Stage 1 / Stage 2 /
Stage 3** everywhere (this DoR, the three briefs — edited in the same change — and all future
references). "Wave" is reserved exclusively for the #254 runtime feature.

Separately: the rollout itself **can and probably should be authored as a #254 waved plan**
(`model-tiering/wave-01-foundation/…`), since the stages have exactly the strict-order,
hard-barrier dependency shape waves enforce, and it would dogfood #254 on real work. That is
an authoring-time choice for the maintainer, not a contract requirement — **OD-D** (§11).

### 2.2 Ratified as-is vs. changed

The three stage briefs are **build-ready in scope, acceptance, and stack** — they are not
rewritten. The organizing change this revision makes on top of them:

- **v1 = static routing; the three dynamic subsystems are deferred to v2** (D18, §10 — the
  organizing decision). The briefs' issues re-bucket as: **v1** = #224 registry + #225 gated
  tagging + #226-**static** resolution + #229 review check + #230-**lite** per-tier spend line;
  **v2 (named bets)** = #227 probes + #228 ladder + #231 steering/`--prefer`. The v2 designs are
  *retained in this doc* (§6.4 probes, §7 ladder, §8 steering — each tagged **[v2 —
  deferred]**) so v2 inherits a ratified spec rather than a blank page.

Settled, and **in v1**:

- **Registry shape settled** (D2, §4): the registry is `promptRunners` *generalized* (a `kind` +
  `routing` extension of the existing blocks), NOT a new sibling section or `providers.json`
  (#224 left this open).
- **The three model axes** (D21, §4.1 — charter D7): a block carries `costly` (bool),
  `strength` (the only totally-ordered axis), and `specialization` (fixed enum), each admitting
  *unspecified*. They are **top-level on the block, not inside `routing`** — a reserved or pinned
  block has a strength too, and the verifier comparison needs it (§4.1).
- **The costly floor** (D22, §6.2 — charter D3): the harness NEVER auto-selects a `costly: true`
  block. Not for its own rung, not for a stronger-rung climb, not (in v2) for a ladder escalation,
  not for a judge bump. Only an explicit task pin — or the `default` pointer, which is itself a
  user assignment (warned, §6.2) — reaches one.
- **The registry is generated, then annotated** (D23, §4.3 — charter D8): `guardrails providers
  init` enumerates what it can and writes the blocks into `guardrails.json` **with the legal axis
  values as `//` comments**, idempotently and without ever overwriting a human annotation.
- **The verifier route is v1 and static** (D24, §6.5 — charter D1/D2/D5/D7/D9/D10): a judge
  guardrail resolves its own route ≥ the actor's, with a **strength** bump when the actor is weak;
  violations are **advisory**, never blocking. Surfaced at **both** boundaries (preflight + JIT),
  both v1.
- **The plan-wide verifier knob is a FLOOR, not a default** (D27, §6.5.1 — charter D4 + D6, which
  it collapses into one concept): `tiering.verifier.minTier` never selects the judge's rung, it only
  refuses one that came out below it.
- **`routing.rank` is retired in favour of ordering by ascending `strength`** (D25, §4.2/§6.2) —
  *the weakest model that can serve the tier goes first*, one ordering axis instead of two with
  opposite polarity. Settled 2026-08-12; a leftover `rank` key raises warning GR2046.
- **`action.effort` corrected** (D3, §5): #200 shipped `action.model` only. Every reference to
  "`action.model`/`action.effort` (already shipped, #200)" in the briefs/issues overstates —
  `effort` is a **new** field this epic introduces (schema in v1, consumed by the v1 resolver).
- **Tagging is gated on tiering being configured** (D19, §5): with no `routing` block anywhere,
  `/plan-breakdown` writes NO tags, NO `tiering` block, and NO classification report lines — a
  single-model user's breakdown is byte-identical to today (Invariant 7, §3).
- **Precedence chain completed** (D9, §6.1): `action.runner` is a full pin (bypasses resolution);
  `action.effort` *alone* overrides the resolved route's effort while tier resolution still
  selects the block (folds in the devil's-advocate F3/F4 findings).
- **Terminology** (D1, §2.1).

Retained but **deferred to v2** (see §10; these were the earlier "Stage 2/3" rulings — they now
gate v2, not v1, and are revisited with #230-lite measurement in hand):

- **Probes advise, never gate** (D8, §6.4) — the #227 ranking/annotation behavior.
- **Escalated attempts draw from the same retry pool** (D5, §7) — the #228 budget rule.
- **Routing-boundary unattended default** (D10, §8.2) — the #231 threshold-prompt behavior.

## 3. Invariants in play

1. **Deterministic guardrails over prompt-judges.** Tier resolution, the ladder, candidate
   ranking, and threshold detection are ALL deterministic harness code. The human-authored
   `routing.notes` prose is context for humans and composed prompts — it is **never parsed to
   make a routing decision**. No LLM ever picks a model.
2. **Harness is the single writer of merged state.** Escalation state is not new state: it is
   *derived* from the journal's attempt history (§7.3), so resume recomputes it — nothing for
   a child to corrupt.
3. **Verdicts from files, never exit codes.** Untouched. The ladder reacts to journaled
   attempt outcomes, which already obey this.
4. **SSOT discipline.** Every schema delta in §12 lands in `02-schemas-and-contracts.md` (and
   the drift-tested `canonical-schema:promptRunners` sentinel mirror) in the same change as
   its code, per stage.
5. **Honest halts.** Resolution never silently routes *weaker* than asked (§6.2); a mis-tagged
   task fails its deterministic gate and surfaces to a human for a re-tag — the model never
   substitutes for the gate. (The v2 ladder changes WHICH model retries use, never WHETHER a
   stuck task surfaces; the v2 threshold prompt's unattended default is the do-nothing status
   quo, loudly logged.)
   **The degrade/halt asymmetry (D26 — the explicit answer to the charter's open consequence):**
   when a rule cannot be satisfied without a `costly` model, the **verifier** rule *degrades* to an
   advisory and the run proceeds (charter D3's derived consequence), but the **actor** route
   *halts* — GR2048 at validate time, `no-route` at runtime. They differ because they are not the
   same kind of thing: a judge is **advisory and never alone** by construction (invariant 1), so a
   degraded judge loses a second opinion while the deterministic gate still certifies; an actor
   route is **load-bearing**, so degrading it would mean shipping work from a model nobody vouched
   for at that difficulty. **Degrade what is advisory; halt what is load-bearing.** Neither
   overspends — which is the property the maintainer actually asked for.
6. **Plain files, light setup.** v1 adds only static config keys and a pure resolver — no
   daemon, no database, no network probe. (The v2 probes are stateless HTTP/CLI queries with an
   in-memory TTL cache, honoring this invariant when they land.)
7. **Single-model / zero-setup back-compat (the load-bearing invariant for THIS epic).** A
   config with **no `routing` block, no `tiering` block, no `kind` (or `kind: "claude"`), and no
   tier tags** MUST produce a **byte-identical routing decision, spend, and execution path** to
   today. Tiering is strictly opt-in: a single-model user who never touches it sees no new
   behavior, no new prompts, no new report lines, and no new failure modes. **Narrowing:** this
   binds *decisions and spend* to be byte-identical; **observability enrichment is exempt and
   additive** — e.g. #349 surfacing the real resolved model in the journal instead of today's
   `"(cli default)"` placeholder is allowed even in a no-tiering run, because it changes what is
   *reported*, not what is *decided or spent*. **Acceptance every stage carries:** (a) the
   existing golden plans run byte-identically; AND (b) a dedicated **"routing-enabled config +
   zero-tag plan"** fixture resolves via the legacy path with **zero tier-resolution activity**
   (and, when the v2 subsystems land, **zero probes and zero threshold prompts**).
   **The verifier half is gated identically (§6.5).** With tiering unconfigured, the judge rule is
   completely inert: no judge resolution, no strength bump, no preflight line, no #229 judge
   finding, no report line. Without this gate a single-model user would start seeing "your judge is
   no stronger than your actor" advisories on a Claude-only setup where actor and judge are
   *literally the same model* — new output on a config that opted into nothing. The
   verifier acceptance fixture is therefore the same fixture as (b), extended with a judge
   guardrail: **zero judge-tiering activity.**

## 4. The registry — `promptRunners` generalized (#224)  [v1]

**Ruling (D2):** there is no new `providers` section. A **runner block IS the routing unit**:
one `promptRunners.<name>` block = one concrete (provider `kind`, `command`/endpoint, `model`,
`effort`, settings) route. A provider exposing three models = three blocks sharing a `kind`.
This reuses the entire existing machinery — naming, `default` pointer, GR2004/GR2008/GR2009
validation, `guardrailOverrides`, `maxOutputTokens`, `env` — and keeps ONE schema under ONE
drift-tested sentinel. Rationale: a parallel `providers.json` would duplicate the
name-resolution and override surface and force a cross-file referential-integrity layer for
zero expressive gain (KISS; the #224 issue itself listed both options).

### 4.1 The three model axes (D21 — charter Decision 7)  [v1]

The charter's review settled that a registry entry carries **three independent axes, not one
blended tier** (the maintainer's words: *"maybe that makes for too many values in one option.
Let's divide it out"*). They are **top-level keys on the runner block**, deliberately **not**
inside `routing`:

| Axis | Type | Ordered? | Who reads it |
|---|---|---|---|
| **`costly`** | **TRI-STATE** — `true` / `false` / **absent = *not stated*** (§12.1, settled 2026-08-15). Three states in the SCHEMA, **two at the predicate**: absent behaves as not-costly, so an un-annotated registry stays routable | no | the candidacy predicate (§6.2) — and `providers init`, which exists partly to find the *not stated* ones and ask (§4.3) |
| **`strength`** | integer ≥ 1, **higher = stronger**; absent = *unspecified* | **yes — the ONLY total order** | candidate ordering (§6.2), the verifier ≥ comparison and its bump (§6.5) |
| **`specialization`** | enum `coding \| planning-reasoning \| general \| unspecified`; absent = `unspecified` | **no — a preference, never an ordering** | the verifier's tie-break among candidates already meeting the required strength (§6.5) |

> **Difficulty does NOT map to strength — it maps to a candidate SET.** Asked directly during Stage 1.5
> ("plan-breakdown indicates a task's difficulty; that should determine the model's strength for the
> first attempt — does that conflict?"). It does not conflict, but the mechanism is one step removed and
> worth stating plainly, because the direct reading is the intuitive one:
>
> **tier tag → the blocks whose `routing.tiers` contains that rung → ordered by ASCENDING `strength` →
> the first one wins.**
>
> So a `hard` task does **not** get "the strongest model". It gets **the weakest model the operator
> declared capable of `hard`** — a cost-minimising default (D25), and what makes defaulting toward the
> cheap end safe is the deterministic gate, not the model. Two consequences follow, and both are
> deliberate:
>
> - **There is no numeric tier→strength mapping, and there must not be one.** Capability is declared by
>   the *operator*, in `routing.tiers`, per block. `strength` only orders candidates that are already
>   declared capable; it never decides *whether* a block can serve a rung. A harness that inferred "hard
>   ⇒ strength ≥ N" would be guessing at capability the operator alone can know, and would silently
>   re-route when someone edited a rank.
> - **A hard task whose only capable blocks are `costly: true` does not escalate — it fails validate**
>   (GR2048), naming the cliff. The harness never reaches for a costly model to satisfy a rung; that is
>   the costly floor, and it holds here exactly as it holds everywhere else.
>
> Strength *does* drive first-attempt selection — as the tie-break inside an operator-declared candidate
> set, not as a translation of difficulty.

**Why top-level and not inside `routing`.** `routing` means *"this block opts into tier
resolution."* A **reserved** block (no `routing`) and a **pinned** block still have a strength — and
the verifier comparison (§6.5) needs the *actor's* strength even when the actor was pinned. Nesting
the axes under `routing` would make a pinned actor's strength unexpressible, which breaks the one
rule the charter exists to state. So: `routing` = eligibility and preference; the axes = properties
of the model itself.

**Every axis admits *unspecified*, and unspecified is not one behavior — it is two, in opposite
directions, on purpose:**

- **Ordering (actor side): unspecified `strength` sorts LAST** among a rung's candidates, ties by
  declaration order. You cannot claim "cheapest sufficient" for a model nobody ranked. *Corollary
  worth stating out loud:* a registry where **nobody** annotates `strength` orders purely by
  declaration order — **exactly** the behavior of the reviewed `rank`-defaults-to-1 design. The
  zero-annotation path is unchanged.
- **Comparison (verifier side): unspecified `strength` counts as WEAK**, via the charter's
  provider-kind fallback — which *over*-warns at worst, and an extra advisory costs nothing (the
  rule is advisory by charter Decision 1).

**The provider-kind fallback is VERIFIER-ONLY (D21a — SETTLED, maintainer-ratified 2026-08-12).**
Charter Decision 7 words the fallback as *"local-inference ⇒ weak, cloud frontier ⇒ not weak"*,
keyed on provider kind. That cannot key on this DoR's `kind` enum, because **`openai-compat` covers
both** a loopback Ollama endpoint *and* any cloud OpenAI-compatible API — that is precisely why it
is one kind (§4). **The ruling, approved:**

> The provider-kind fallback is used **only** on the verifier side (§6.5), where it reads
> `kind != "claude"` ⇒ *weak-unless-declared*. It is **never** used for actor ordering.

The asymmetry is what makes the fallback safe rather than merely convenient: on the verifier side a
wrong guess costs **one spare advisory** on a rule that is advisory anyway; on the actor side the
same wrong guess would **misroute real spend**. So the guess is allowed exactly where being wrong
is cheap, and forbidden exactly where it is not. A user who dislikes the guess overrides it by
declaring `strength` — which is the entire point of the axis existing.

**The axes are per-BLOCK, therefore per-EFFORT — and that is a feature.** Because a block IS a
(kind, model, effort) route (§4), "the same model at `low` and at `xhigh`" is two blocks and can
carry **different** `costly` values. A frontier model at minimal effort need not be marked costly
while its `xhigh` sibling is. No extra mechanism was needed for this; it falls out of D2.

Malformed axes (wrong type, non-positive `strength`, a `specialization` outside the enum) are
**GR2045**.

### 4.2 The routing block, and the retirement of `rank` (D25)  [v1]

Three new optional keys per block (full JSON in §12.1):

- **`kind`** (D4) — the provider discriminator. **Default `"claude"`** (every existing config
  keeps working unchanged — the Stage-1 back-compat acceptance). v1 implements only
  `"claude"`; **`"openai-compat"` is the reserved seam #223 fills** (one kind covering
  Ollama / llama.cpp / LM Studio / vLLM — they share the wire protocol); `"codex"` /
  `"openrouter"` / **`"local"`** are reserved names, unassigned. (**`"local"` was missing from
  this list until 2026-08-15** — Stage 1 shipped it as a fourth reserved token and Stage 1.5
  kept it rather than make an unrequested breaking change, so the enum of record is
  `claude | codex | openrouter | local | openai-compat`. The omission was this document's,
  not the code's.) A `kind` that is unrecognized OR recognized
  but not yet implemented in the installed harness fails `guardrails validate` with
  **GR2044**, naming the value and the currently supported set — an honest halt at validate
  time, never a silent fallback to Claude. `PromptRunnerRegistry.FromConfig` switches on
  `kind` to construct the runner class (the seam its own doc comment already names).
- **`effort`** (D3) — an opaque, per-block thinking-effort knob (e.g. `"low"`, `"xhigh"`).
  Opaque to the harness: shape-validated only (GR2050, mirroring GR2030's `model` check) and
  **translated by the runner CLASS** into whatever its CLI/API exposes — the spelling is
  quarantined exactly like `maxOutputTokens` → `CLAUDE_CODE_MAX_OUTPUT_TOKENS`. Wanting the
  same model at two efforts = two blocks (`"opus"`, `"opus-xhigh"`).
- **`routing`** (D6) — opts the block into tier resolution. **Absent ⇒ the block is NEVER a tier
  target** — reachable only explicitly (`action.runner`/`action.model`) or as the `default`
  pointer, exactly today's behavior. Shape: `{ "tiers": [...], "notes": "…" }` where
  `tiers` (required, non-empty subset of the tier enum) is the **machine-consumed** part — which
  tiers this route may serve; `notes` (optional prose) is
  surfaced to humans (`providers status`, review context) and MAY be appended to a composed
  prompt as context — **never parsed** (invariant 1). Malformed routing (empty/unknown `tiers`,
  wrong types) is **GR2047**. The prose-vs-tags question is thereby
  answered: **both, with a hard deterministic/advisory split** (D6).

**`routing.rank` is RETIRED (D25 — settled 2026-08-12, was OD-F).** Revision
2 gave `routing` a `rank` (lower wins) to order same-tier candidates. Now that `strength` exists as
a declared, totally-ordered axis (§4.1), `rank` would be **a second ordering axis with the opposite
polarity** — "rank 1 beats rank 2" next to "strength 5 beats strength 2" is a bug waiting to be
written, twice, by different people. The maintainer chose to drop it. The settled rule:

> **Same-rung candidates are ordered by ASCENDING `strength` — the weakest model that can serve the
> tier goes first.** Unspecified `strength` sorts last; ties break by declaration order.

**Why ascending, said out loud, because the direction is the whole feature.** This is a
**cost-minimising default**: the entire premise of #201 is that spending a frontier model on
routine work is waste, and *"weakest model that can serve the tier goes first"* is that premise
expressed as an ordering rather than as a preference list somebody has to maintain. What makes it
safe to default toward the cheap end is the same thing that makes the whole epic safe — **the
deterministic gate certifies, not the model**: if the weakest eligible model produces bad work, its
guardrails fail it and the task halts honestly (§14, row 01). A design without that gate could not
afford this default.

**Replacement idiom — how to say what `rank` used to say.** To express *"sonnet should serve
`medium`, local-kimi should not"*, **remove `medium` from local-kimi's `routing.tiers`.** That is
strictly more honest than out-ranking a block while still declaring it eligible: it states the
capability judgment ("kimi cannot be trusted with medium work") in the place the design reads
capability judgments, instead of hiding it in a preference number. Eligibility says *may*;
`strength` says *how strong*; nothing needs to say *prefer*.

**Migration — for anyone who already wrote `routing.rank`.** No config in the wild has one (nothing
has shipped; `rank` existed only in revision 2 of this document), but the rule if a draft does:
**delete the `rank` key and, for any block you were ranking DOWN, remove the tiers you were ranking
it out of.** A leftover `rank` key is simply an unknown property — the loader ignores unknown keys,
so it neither errors nor does anything, which is the quietest possible failure. **Stage 1 therefore emits a
validate WARNING — GR2046 `RetiredRoutingRank`** — rather than silently ignoring the key. The
hazard it closes is real and is exactly the kind this repo refuses to ship: a config carrying
`rank` would have its candidate ordering **silently change** from rank order to strength order,
with nothing to tell the author their preference stopped being honored. A retired field that looks
like it still works is the trap this consolidation existed to remove.

**Reserved-model pattern (D20, now DECLARED rather than incidental — answers review comment 7,
"reserve Fable for /plan-breakdown; re-attempts must never reach it").**

The most useful thing this revision found is that **review comment 7 and charter Decision 3 are
the same requirement**, arrived at from opposite ends:

- Comment 7 (the actor side): *"Bleeding-edge frontier models like Fable should be reserved… I
  don't want re-attempts to reach for Fable at all."*
- Charter Decision 3 (the verifier side): *"The harness NEVER auto-selects a costly model — only
  the user may assign one."*

Both say: **there exist models the harness may name only when a human named them first.**
`costly: true` is the declared spelling of that, and it answers both. Revision 2 answered comment 7
with a *convention* — "omit the `routing` block" — and its own devil's-advocate self-critique
flagged that as "a convention a reader will miss" (the DA pass itself missed it). A boolean the
generated registry (§4.3) puts in front of the user, with the legal values in a comment, is not
missable.

**So there are two reservation forms, and they are not equivalent:**

| Form | Says | Use it when |
|---|---|---|
| **`costly: true`** (declared) | "the harness may never *choose* this; only a human may assign it" | the intended, discoverable reservation — Fable, a bleeding-edge frontier model |
| **omit `routing`** (incidental) | "this block simply isn't in the tier system" | a block that has no place in routing at all — a one-off, a legacy block |

Both make a block non-selectable by the resolver (§6.2 uses ONE candidacy predicate covering both).
A block can be pinned explicitly (`action.runner`/`action.model`) under either form.

Two rules make the reservation airtight, and they now cover **both** forms:

- **A non-routable block must NOT be the registry `default` pointer** — otherwise an untagged task
  with no `defaultTier` falls to legacy resolution = the default runner = the reserved model,
  and the reservation evaporates through the back door. `validate` **warns** (**GR2051**) when a
  `costly: true` **or** `routing`-less block is named `default` in a config that declares tiering
  (the same net #229 provides at review time). *(This is why the worked example (§14) never makes a
  reserved block the default.)* **Note the deliberate limit of this rule:** naming a block `default`
  IS a user assignment — a plan-wide one — so it does not *violate* the costly floor; it is warned
  because untagged work would then silently spend a costly model, which is the §5 cost disclosure
  with a flag on it.
- **`/plan-breakdown`-time model choice is OUTSIDE this registry entirely.** The breakdown runs
  in the user's own Claude session, not through `promptRunners`; reserving a model *for authoring*
  is a session/tooling choice, not a routing config. The DoR states this so a reader does not
  expect a `promptRunners` knob for it. *(This is worth re-reading against comment 7: the harness
  cannot make Fable the breakdown model, and `costly: true` cannot make it one either. What
  `costly` guarantees is the half the harness DOES control — that no run ever reaches for it.)*

**`costly: true` + `routing` on the same block is inert, and warned (GR2052).** The routing can
never apply, because the candidacy predicate excludes costly blocks first (§6.2). It is a warning
rather than an error so the two mechanisms compose instead of fighting: if excluding that block
leaves a *used* tier unserved, **GR2048** (unservable tier) fires as an error and names the cliff
precisely.

**The cliff is INTENDED — settled 2026-08-12 (was OD-G).** Marking your only `hard`-capable block
`costly` makes `hard` unservable, and that is a **validate-time ERROR (GR2048)**, not a warning to
route around. The maintainer's words are taken literally: *"They should never be chosen by the
harness, only the user can specify to use them for a task."* There is no "expensive but still
routable" middle setting, no dial, and no split into `costly`-for-accounting plus
`reserved`-for-the-floor. The consequence is a feature: the config now states, checkably, **"hard
tasks must be pinned by a human"**, and it says so *before a token is spent* rather than by
surprising you with a bill. See §14 for the exact error a user meets.

**Deferred to v2 (with the ladder):** the DA pass proposed a `routing.escalationTarget: false`
field to express *"may serve a tier on first attempt but never RECEIVE a ladder escalation."*
That field is only meaningful once the ladder exists, so it is deferred to v2 with #228 (§7). In
static v1 the two reservation forms above fully cover the maintainer's requirement (a reserved
model is unreachable by any resolver, first attempt or retry). **Note for v2:** `escalationTarget`
is now the *third* non-selectability concept; v2 should check whether `costly` already subsumes it
before adding a field.

**Tiering activation — configured vs. active (D13, absorbing DA F6 + the KISS gate):**

- **Configured** iff ≥ 1 block declares `routing`. This is what gates *tag authoring* (§5) and
  validation (a plan carrying tier tags with NO `routing` block anywhere is tiering-inert →
  **warning GR2049**, and the plan runs exactly as today).
- **Active for a task** only when that task will *actually resolve through routing* — i.e. it has
  an **effective tier** (a tag or `defaultTier`) **AND** a serving routing block exists. Tier
  resolution, and (in v2) any probing or threshold prompt, fire **only** for such work — **never
  merely because the config declares a `routing` block**. A run whose remaining tasks are all
  untagged resolves entirely via the legacy path and does nothing tiering-specific. This is what
  makes Invariant 7 *provable*: activation is plan-scoped, not config-scoped.

This makes the whole epic opt-in and every existing plan byte-compatible.

### 4.3 The registry is generated, then annotated — `guardrails providers init` (D23, charter Decision 8)  [v1]

The charter settled that the registry is **not hand-typed from memory**: *"Guardrails knows the
model providers and should be able to enumerate its models and provide them for configuration…
in a jsonc file which will have comments to indicate the enum values allowed to attach to a
model."* Three axes with legal enums are exactly the kind of schema nobody remembers — the values
must be discoverable **in the file being edited**. So Stage 1 ships a command:

**`guardrails providers init`** — enumerates what it can, and writes/updates the `promptRunners`
blocks in `guardrails.json` with the legal values for `costly` / `strength` / `specialization` /
`routing.tiers` carried as `//` comments beside each key.

Five rulings, each of which an implementer would otherwise have to guess:

1. **It writes into `guardrails.json`, not a sibling `.jsonc` file.** The charter said "a `.jsonc`
   config"; the substance of that is *comment-bearing JSON*, and `guardrails.json` **already is**
   comment-bearing — `PlanJson.Options` sets `ReadCommentHandling = JsonCommentHandling.Skip` and
   `AllowTrailingCommas = true`, precisely because humans hand-edit these files, and the committed
   SSOT example already uses `//` comments. A second file would need a precedence-and-merge story
   for zero gain (KISS). *Verified against `src/Guardrails.Core/Loading/PlanJson.cs` at authoring
   time — this is the one charter detail that could have blocked implementation and does not.*
2. **It NEVER invents a model name — a hard rule, ratified 2026-08-12 (was OD-E).**
   `openai-compat` has a real enumeration surface (`GET /v1/models`). **The Claude CLI may not** —
   the same feasibility risk as OD-C's usage probe, which charter Decision 8's *"Guardrails knows
   the model providers"* assumed away. The maintainer chose **degrade honestly** over the two
   alternatives (ship a curated model list; fail the command). **The settled ruling:**

   > A model identifier in the registry may only come from **a provider that reported it** or **a
   > human who typed it**. `providers init` may never synthesize one — not from a shipped list, not
   > from a heuristic, not from a previous release's knowledge.

   **The reason this is a hard rule and not a nicety:** a registry entry is not documentation, it
   is a **routing target**. A fabricated or stale model id would be selected by the resolver and
   **spent against at a model that may not exist** — an authoring-time guess turning into a runtime
   failure, or worse, silent substitution by a provider that resolves unknown names loosely. The
   generator has no way to know, so it does not guess. This is the same rule as GR2044's refusal to
   silently fall back to `claude` for an unrecognized `kind` (§4.2), applied one layer earlier.

   **What it does instead:** for a `kind` with no enumeration surface, it emits the blocks
   **already present** in the config, fully annotated, plus an explicit
   `// could not enumerate models for kind "claude" — add blocks manually; the legal axis values
   are above` note. It **never fails the command** — the annotation half of its job still succeeds,
   which is what makes "degrade" the right word rather than "give up". Honest halts, applied to a
   generator.
3. **It is idempotent and never overwrites a human annotation.** Re-running adds *missing* blocks
   and *missing* comment annotations; it never rewrites an axis a human has set, never reorders
   blocks, and never deletes. A generator that clobbers the annotation it exists to solicit is
   worse than no generator.
4. **It is out-of-band, never part of a run.** This matters for **Invariant 6** ("no network probe
   in v1"), which it superficially appears to violate: enumeration is a user-invoked command that
   touches the network *outside* any run, exactly like `git remote update`. **The run path adds no
   probe in v1** — that is what Invariant 6 constrains, and it holds.
5. **It writes config, so it obeys the review discipline.** Output is presented as a **diff for
   the human to accept** — the same "everything is a reviewable draft" posture as a generated task
   folder. It is not a silent config mutation.

`guardrails providers status` (the *live-state* inspector) stays **v2 with the probes** (§6.4).
Two verbs in one noun-space landing in different versions is deliberate: `init` needs only a
model list, `status` needs a usage surface, and only the second one is blocked on OD-C.

### 4.4 The #223 seam (defined here, not designed here)

#223 delivers an `IPromptRunner` class for `kind: "openai-compat"`: constructor
`(name, endpoint/command, model, effort, settings)` from its block; MUST preserve the verdict-file
contract (SSOT §4.2/§5), the `PromptFailureKind` classification quarantine (its own vendor error
strings live in its class, like `ClaudePromptRunner`'s), populate the same §7 provenance
fields, and report cost as absent (tokens only) unless its API provides one. When it lands,
GR2044's supported set grows — no other contract moves. Its internals (auth, streaming,
endpoint probing) are #223's own design space.

## 5. The tier model (#225)  [v1]

**Ruling (D7): the tier enum is `easy | medium | hard` — final for v1.** Closed, lowercase,
ordered `easy(1) < medium(2) < hard(3)` (the resolver's ordering; also the v2 ladder's rungs).
An unrecognized value anywhere (GR2043) is a validation error. Three levels is deliberately
coarse: the tag must stay a cheap, stable judgment a human can make without knowing what is
registered (#201's rationale); finer gradations would re-couple tagging to model knowledge.

**On "3 levels / low-medium-high / does registration need human input?" (review comment 3):**

- **Difficulty, not strength.** `easy|medium|hard` names **task difficulty** (a property of the
  *work*); `low/medium/high` would name **model capability** (a property of the *model*). They
  are deliberately different axes: the task carries a difficulty tag, and each block declares
  which difficulties it is willing to serve via `routing.tiers`. Keeping the tag about the work
  is what lets a human tag without knowing the registry.
- **Registration IS the human declaring ability.** There is no auto-inference of a model's
  "thinking level" (invariant 1 forbids an LLM judging that). The human expresses it explicitly
  when they register a block — `routing.tiers` (which difficulties it serves), **`strength` (how
  capable it is), `costly` (whether the harness may choose it at all), `specialization` (what it is
  good at)**, `effort` (its thinking knob), `notes` (prose rationale) — on a registry the harness
  **generates and annotates for them** (§4.3). That *is* the human input on capability the comment
  asks about, and revision 3 makes it a filled-in form rather than a remembered schema.
- **Three words that are NOT synonyms, and the whole design turns on it.** **Tier** = how hard the
  *work* is (`easy|medium|hard`, on the task). **Strength** = how capable a *model* is (an integer
  rank, on the block). **Specialization** = what a model is *good at* (an enum, on the block; a
  preference, never an ordering). A "bump" is always in **strength** — never in tier: bumping the
  tier would mean *"pretend the work is harder"*, a category error (§6.5 makes this explicit,
  because the charter's prose uses "one tier above" and "one strength rank above"
  interchangeably and they are not the same operation).
- **A 4th tier is additive-later, at zero cost.** "Final for v1" is not a one-way door: adding a
  tier is a purely additive schema change (a new enum value + blocks opting into it). We hold at
  three because the *deeper* fix for "more thinking levels" is the reserved-model / role
  restriction pattern (§4), not more difficulty rungs.

Where tags live:

- **`action.tier`** (task.json, prompt actions only) — mirrors the `action.model` pattern.
- **`tier`** frontmatter key on a `*.prompt.md` **judge guardrail** (SSOT §4.2 frontmatter, joining
  `runner`/`maxTurns`) — so #225's "and any surviving judge-guardrail" has a concrete surface.
- **`tiering.defaultTier`** (guardrails.json, optional) — the plan-wide default for untagged
  prompt actions (including one a human hand-adds later). **There is NO built-in default**
  (answering review comment 2, "is medium the assumed default if /plan-breakdown forgets?"): the
  `"medium"` shown in the §12.1 example is an *example value*, not a fallback. **Absent ⇒ an
  untagged task follows the legacy resolution path** (runner default), even when tiering is
  configured — the feature never captures a task nobody classified (D13).
  - **Cost disclosure.** "Legacy resolution" means the *default runner*, which is usually the
    frontier block — so an untagged, hand-added task silently spends *frontier*. This is the
    correct **conservative** default (never route work to a model nobody vouched for; defaulting
    untagged to the *cheapest* block would route an unclassified hard task to a 7B and fail its
    gate), but it is capability-safe, not cost-safe. **#229 is the cost-side net**: it flags a
    prompt task carrying neither a tier nor an explicit pin, before the run.

**Gated tagging (D19 — makes Invariant 7 provable at the authoring layer).** `/plan-breakdown`
knows whether routing exists, because it produces `guardrails.json`. Its tagging behavior is
**gated on tiering being configured**:

- **Tiering configured** (≥1 `routing` block): the skill classifies every prompt-action task
  (and surviving judge guardrail), writes the `action.tier`, and **surfaces each classification +
  a one-line why in the breakdown report** (the #42 surface-the-choice discipline — never
  silent). Its quality-bar checklist gains the doctrine entry (mirroring #94's
  maxTurns-by-archetype precedent).
- **Tiering NOT configured** (no `routing` block anywhere — the single-model default): the skill
  writes **NO `action.tier` fields, NO `tiering` block, and NO classification report lines**, and
  **GR2049 cannot fire**. A single-model user's breakdown is therefore **byte-identical to
  today** — the authoring half of Invariant 7.

## 6. Attempt-launch resolution — the static resolver (#226-static)  [v1]

Runs immediately before **every** attempt launch, including retries. Deterministic, in the
harness, replacing today's two-level `ResolveModelForDisplay(task.Action.Model, runnerModel)`
fallback (`TaskExecutor.cs` ~1027–1032). **In v1 it is a pure function of (effective tier +
registry)** — no probe consultation, no ladder-awareness, no steering — so it yields the *same*
block on every attempt of a task. The dynamic inputs (probes §6.4, ladder §7, steering §8) are
v2 and slot into this same resolver without moving the seam.

### 6.1 Precedence (D9 — the full pin/config order; folds in DA findings F3/F4)

1. **Full pin — `action.runner` or `action.model`** (task.json) — explicit always wins and
   **bypasses tier resolution entirely** (and, in v2, the ladder — a pinned task never escalates,
   D14). `action.runner` selects a named block; `action.model` overrides the model string.
   Shipped semantics unchanged. **This is the sanctioned route to a `costly` model** (§6.2): a pin
   is a human naming a model for a task, which is exactly what charter Decision 3 permits — the
   floor constrains the harness's choices, never the human's. No warning, no dial, no ceremony.
   *(Note for the verifier rule: a raw `action.model` pin overrides the model string but not the
   block, so the pinned actor's `strength`/`kind` still come from its block — §6.5 has the data it
   needs even here.)*
2. **Tier resolution** (when the task has an effective tier and a serving block exists):
   effective tier = `action.tier` (or judge frontmatter `tier`) ?? `tiering.defaultTier`;
   rung = the effective tier (**in v2**, adjusted by the ladder §7); route = best candidate block
   (§6.2) (**in v2**, biased by steering §8). **`action.effort` *alone* (no full pin) is NOT a
   bypass** — tier resolution still selects the block, and the effort override is applied *to the
   resolved route's effort* (so `{ "tier": "medium", "effort": "xhigh" }` means "route by tier,
   but think hard"). This is the F4 correction: only `action.model`/`action.runner` are full
   pins; `effort` mirrors `model`'s *shape* but not its *bypass*.
3. **Legacy fallback** — no effective tier, or no block serves it: `promptRunners.<name>.model`
   else CLI default, exactly today.

**Validate warning (GR-warning, from DA F3):** when a **full pin and a tier coexist** on the same
action (`action.runner`/`action.model` *and* `action.tier`), `validate` warns — the tier is dead
weight the pin overrides, usually an authoring mistake. (A pin + `action.effort` is fine — see
item 2.)

### 6.2 Candidate selection — never weaker than asked, never costly without you  [v1]

**ONE candidacy predicate, written once, used by everything (D22a).** This is a correctness
requirement, not tidiness: `validate`'s GR2048 check, the runtime resolver, the `no-route` outcome,
the §6.5 judge resolution, and (in v2) the ladder must all agree on *which blocks count as serving
a rung*. If GR2048 counts a costly block as "serving `hard`" and the resolver does not, validation
passes and every hard task dies at runtime on `no-route`. So:

> **`Candidates(R)` = blocks where `routing` is present AND `R ∈ routing.tiers` AND `costly` is
> not `true`.** Ordered by **ascending `strength`** (unspecified last), ties by declaration order.
> The first candidate wins.

If `Candidates(R)` is empty, climb to the nearest **stronger** rung with a non-empty candidate set
(loud log line + provenance records the climb). *(In v2, steering §8 prepends a bias before the
strength order, and probe state §6.4 sinks exhausted blocks — but neither the never-weaker floor
nor the costly exclusion may be re-opened by any later version.)*

**The costly floor (D22 — charter Decision 3).** `costly: true` blocks are excluded from
`Candidates(R)` at **every** rung — their own, a climbed-to stronger rung, a v2 ladder escalation,
and a §6.5 judge bump. This is not a ceiling the resolver negotiates; it is a **hard floor on
harness autonomy**, and it is the one rule in this design with no override, no `--force`, and no
autonomy-policy dial. The only paths to a costly model are:

1. an explicit **task pin** — `action.runner` / `action.model` (a per-task user assignment); or
2. the registry **`default` pointer** (a plan-wide user assignment) via legacy resolution — which
   is sanctioned but warned (GR2051, §4.2).

Everything else is the harness choosing, and the harness does not choose.

**Routing DOWN a rung is never automatic.** In v1 the only lever below the never-weaker floor is
**halt-and-edit-config** (change a block's `routing.tiers`, re-run). *(The v2 steering design
adds a human-sanctioned mid-run "serve tier X from block Y for the rest of this run" option; that
is the DA-F2 route-down contradiction, resolved by deferral — there is no half-built downward
lever in v1.)*

**Nor is there a costly-model degrade — and that is the deliberate asymmetry (D26).** When a rung's
candidate set is empty because the only capable block is `costly`, the actor route does **not**
quietly reach for it and does **not** quietly drop to a weaker rung. It **halts**: GR2048 at
validate time (zero spend, before the run), `no-route` at runtime. Contrast the verifier rule
(§6.5), which in the same situation *degrades to an advisory and proceeds*. See invariant 5 for
why the two differ — degrade what is advisory, halt what is load-bearing. **Both honor the
maintainer's actual constraint: neither ever overspends.**

Statically, `validate` errors (**GR2048**) when any *used* tier (a task tag, frontmatter tag, or
`defaultTier`) has no **candidate** rung at-or-above it — the only config where resolution would
have to route down. Note this now fires for a *second* reason as well as an empty registry: every
block that could serve the rung is `costly: true`. The diagnostic must say **which** — "no block
serves tier `hard`" and "the only blocks serving tier `hard` are marked `costly`, which the harness
may never select; pin them per task or clear the flag" are different problems with different fixes.

**The `no-route` defensive outcome [v1].** The **`no-route`** attempt outcome (§12.4) exists only
for the defensive residual — resolution finds literally zero registered candidate blocks at
runtime for a used rung (a config gap GR2048 should have caught) — and settles needs-human with
an actionable "register a provider serving tier ≥ R" message. It is cheap, honest, and independent
of probes, so it stays in v1.

### 6.3 Provider unavailability — connection failures ride the shipped pause [v1]

Answers review comments 5 ("what if the internet is down but local inference still responds") and
6 ("availability re-checks should use exponential backoff"). Without a ladder (v2) the
budget-burning "climb to progressively more expensive, equally-unreachable models" spiral the DA
pass warned about **cannot happen in v1** — but the core ruling still matters:

- A **connection-level failure** at launch (DNS failure, connection refused, TLS timeout, a
  missing CLI) is classified `Transient`/*unavailable* and routed to the **shipped #115
  transient-pause machinery** — **no budget consumption**, the existing bounded exponential
  backoff (2s→60s, honoring any parsed reset hint), bounded by `transientPauseBudgetSeconds`. That
  is where comment 6's exponential-backoff requirement is already satisfied — re-checking a downed
  provider reuses the shipped pause loop rather than a new re-probe timer.
- During a **frontier outage with local up**: `easy`/`medium` continue on their serving *local*
  blocks (the static resolver already routes them there — no special case), while a **`hard`**
  task with no local block that serves `hard` **pauses and waits** rather than routing down —
  the never-weaker-than-asked floor (§6.2) holds. The task surfaces `rate-limited`/needs-human
  honestly if the pause budget is spent.
- **v1 scope note:** the harness classifies a connection failure using the *shipped*
  `PromptFailureKind` quarantine; whether that quarantine already catches every DNS/refused/TLS
  shape, or needs a small additive `Unavailable` classification, is a v1 implementation detail for
  the harness developer (the DA pass flagged that `Transient` matches 429/503/529 but may miss a
  bare DNS failure). No new probe enum is introduced in v1 (the DA's `unreachable` probe state
  belongs with the v2 probes).

### 6.4 Probes advise, the pause machinery enforces (D8, #227)  [v2 — deferred]

**Deferred to v2 (a named bet, §10). Retained here as the ratified probe design.** Probe state
(§6.4.1) would **re-order and annotate** candidates (an exhausted provider's blocks sink below
serviceable ones; `unknown` counts as serviceable); it never vetoes a launch. If every candidate
at-or-above the rung is probe-exhausted, the harness launches the best candidate anyway and lets
the **shipped** transient-pause machinery (#115) ride the limit out — bounded by
`transientPauseBudgetSeconds`, settling `rate-limited`/needs-human honestly on exhaustion.
Rationale: probes are advance estimates and can be stale/wrong; the runner's live 429 is ground
truth, and its handling already exists — a parallel probe-gated failure path would be a second,
weaker copy of it.

#### 6.4.1 The probe classes (#227)  [v2 — deferred]

Per-`kind` probe classes (`IProviderProbe`), returning
`{ status: ok | nearing-limit | exhausted | unknown, headroom?, detail, probedAt }`:
Claude = the CLI/account's usage surface where one exists (weekly-plan %, 5-hour window);
openai-compat = endpoint reachability/load; a kind with no usage surface returns `unknown`
(never fails the run — the *degraded/absent usage surface is honestly surfaced*, review comment
4). **Rulings (D11):** probes are deterministic HTTP/CLI queries — **never prompt spend** (an LLM
call is not a probe); cached in-memory per provider with TTL `tiering.probeCacheSeconds` (default
**60**, GR2054 if ≤ 0), with **consecutive-failure TTL doubling** (cap ~15 min) so a dead endpoint
is not re-probed every minute all run (DA comment 6); probed lazily at resolution and at run
start, with a small hard per-probe timeout; observe-only (not journaled — they surface in
`decisions[]` context, threshold prompts, and a new **`guardrails providers status`** command). A
free retrospective signal is available at zero probe cost: #115's runner quarantine already parses
reset hints / limit phrases out of live responses, so a prior attempt's parsed limit signal is
per-window data the probe layer can ingest. **Feasibility of a *stable* Claude usage probe is the
reason probes are deferred, not merely phased** — see OD-C (§11), now a v2 decision.

### 6.5 The verifier route — "a prompt may propose, only an equal-or-stronger judge may vouch" (D24)  [v1]

**Source: [`model-tiering-verifier.charter.md`](model-tiering-verifier.charter.md), fully reviewed,
10 numbered Decisions.** That charter owns the rationale and the review record; this section owns
the contract, and where the two differ **this DoR wins** (the same rule as the three stage briefs).

**The failure it prevents** is the model-layer analogue of #382 (passing-but-blind): if a task runs
on a local 32B and its *judge* guardrail runs on the same local 32B, a plausible-but-wrong
implementation and a plausible-but-wrong "looks good to me" can agree, and the run goes green over
broken work. **Scope, stated first because it bounds everything below:** this governs ONLY the
layer where a model renders the verdict — prompt-judge guardrails, a terminal `<plan>/guardrails/`
phase carrying a judge, and (charter Decision 5) the autonomous review-gate (#361) and overwatcher
(#269). Deterministic guardrails run no model and are untouched. Tiering does not weaken
deterministic-first; it hardens the one place a model's opinion is load-bearing.

**Resolution rule (charter Decisions 2 + 7, made precise).** A judge guardrail resolves its own
(provider, model, effort) at attempt launch, alongside the actor, in the same `TierResolver`:

1. **Explicit wins.** A judge's frontmatter `tier` (SSOT §4.2) or `runner` pin resolves like an
   action's (§6.1). No rule below applies.
2. **Otherwise the judge's rung = the actor's effective rung.** Not the actor's *strength* — the
   *rung*, because rung is what `routing.tiers` is expressed in.
3. **The bump is in STRENGTH, never in tier (D24a — a real ambiguity in the charter).** The charter
   says "bumped one tier ABOVE" in Decision 2 and "one strength rank ABOVE" in its diagram. Those
   are different operations and only one of them is coherent: bumping the *tier* means "pretend the
   work is harder", which contradicts D7's difficulty-≠-strength split and would drag the judge
   into a rung nobody declared for this work. **Ruling:** when the actor is *weak*, the judge is the
   **weakest candidate at the actor's rung whose `strength` is strictly greater than the actor's**.
   If none exists, see (5).
4. **"Weak" is decided by `strength` when declared, and by the provider-kind fallback when not**
   (charter Decision 7) — with §4.1's correction that the kind fallback is verifier-only and reads
   `kind != "claude"` ⇒ weak-unless-declared. Equal-and-strong needs no bump (Opus judging Opus is
   a real check); equal-and-weak does (one blind spot talking to itself).
5. **It degrades; it never overspends (charter Decision 3's derived consequence — CONFIRMED here).**
   The bump obeys the costly floor (§6.2): if the only stronger block is `costly: true`, **the
   judge stays at the actor's route and the #229 advisory fires.** The run proceeds. This is the
   consequence the charter's disposition 2 flagged for confirmation, and this DoR confirms it *and*
   states the actor-side counterpart the charter left implicit: **the actor does NOT degrade — it
   halts** (§6.2, invariant 5).
6. **Specialization breaks ties, and only ties** (charter Decision 7 + `specialization-values`).
   Among candidates that already meet the required strength, prefer `planning-reasoning`, then fall
   back to the §6.2 ascending-strength order. It can neither satisfy nor violate ≥, and a mismatch
   is never a finding.
7. **`guardrailOverrides` compose with the resolved judge block, not the actor's** (implementation
   ruling — an implementer will get this backwards otherwise). The judge's route is resolved first;
   the `guardrailOverrides` that then apply are **that block's**, since overrides are a per-block
   verdict profile (permissions/tools/turns). Applying the *actor's* block's overrides to a judge
   running on a different block would silently mis-profile every bumped judge.

**Surfacing is ADVISORY, at BOTH boundaries, and BOTH are v1 (charter Decisions 1, 9, 10 —
ratified 2026-08-12).**

A judge weaker than its actor, or **equal-and-weak**, is surfaced as a **#229 review finding**, as a
**startup preflight warning line**, and as a **per-attempt JIT re-check**. Never a hard error, never
a load-time refusal, never a halt — in attended *or* unattended mode (charter Decision 10). The
harness does not block on a model-quality opinion.

**What the JIT re-check does in static v1, stated plainly.** An earlier draft of this DoR proposed
deferring the JIT half on the grounds that its stated justification — *"a tier reached by graduating
mid-run is invisible to any preflight"* — has no referent until the v2 ladder exists. **That
deferral was overruled: Decision 9's "both" is v1.** The honest account of what the second boundary
buys in a static v1, so nobody has to guess:

1. **The preflight is a MODEL of the resolver; the JIT check IS the resolver.** The preflight
   *predicts* each task's (actor, judge) pair from the plan and registry; the JIT check reports the
   pair the resolver **actually returned** at attempt launch. In a correct implementation they
   agree, and **that agreement is the point** — the two are a mirror-check, and any disagreement is
   by definition a resolver bug that no amount of preflight sophistication could catch. This repo
   has already paid for the general version of this lesson (#382: a static check that mirrors the
   real path certifies green while the real path is broken). A preflight that is the *only* check
   is a fake mask over the composition root.
2. **It is the only boundary that sees a mid-run mutation.** A resumed run whose `guardrails.json`
   was edited between sessions, an overwatcher-applied action change (#269), a human hand-edit
   between waves (#254) — none of these existed when the preflight ran. In static v1 the resolver
   is a pure function of *(tag + registry)*, but neither input is frozen for the life of a run.
3. **It costs almost nothing, because §9.3 already requires the work.** The `judge {...}` provenance
   object is written per attempt regardless — which means the judge route is *already* resolved at
   every attempt launch. The JIT re-check is one comparison over values the harness has already
   computed, not a second resolution pass.
4. **v2 then adds a trigger, not a subsystem.** When the ladder lands and the actor can graduate
   mid-run, the JIT check needs no new machinery — it starts seeing a moving actor and re-resolving
   the judge against it (charter Decision 6).

**De-duplication ruling (the one consequence this creates).** With three surfaces reporting one
condition, a single weak judge could produce a #229 finding, a preflight line, and one advisory per
attempt — noise that trains people to ignore it. So: the **preflight** emits one pre-run summary
line per affected task; the **JIT re-check** records `judge.advisory` in that attempt's provenance
**always**, but emits a **log line only when the observed pair differs from what the preflight
predicted** (case 1 or 2 above) — the interesting case, and the only one the preflight did not
already say. The run summary aggregates from provenance, so nothing is lost by the quieter log.

### 6.5.1 The verifier floor — `tiering.verifier.minTier` (D27)  [v1]

**Settled 2026-08-12 (was OD-H), and it CHANGED the design.** Charter Decision 4 asked for "both
granularities: a per-plan default verifier tier plus a per-task override". Revision 3 read the
per-plan half as a *default* — a plan-wide override of the rule in step 2. **The maintainer's
answer makes it a FLOOR instead**, and the distinction is not cosmetic:

| | A default (what revision 3 had) | A floor (what is settled) |
|---|---|---|
| Does it choose the judge's tier? | **Yes** — it replaces the rule | **No, never** — the rule in steps 2–3 still chooses |
| When does it act? | Always, unless a per-judge pin overrides | **Only** when the chosen result came out *below* it |
| Can it lower a judge? | Yes (a plan-wide `easy` would drag every judge down) | **No.** It only ever raises |

> **`tiering.verifier.minTier` (optional): the resolved judge may never end up below this rung.**
> It never selects; it only refuses a result that came out too low.

**Resolution order, restated with the floor in place.** Steps 1–3 of §6.5 are unchanged; the floor
is applied *after* them:

1. Frontmatter `tier` / `runner` pin → wins (see the pin note below).
2. Judge rung = the actor's effective rung.
3. Weak-actor **strength** bump.
4. **Floor:** if the rung from (2)–(3) is below `minTier`, raise it to `minTier` and re-select from
   `Candidates(minTier)`. **Never the reverse** — a result at or above `minTier` is untouched.
5. Costly floor (§6.2) applies to every selection above; `specialization` breaks remaining ties.

**Why this is reachable in static v1 — correcting revision 3's own reasoning.** Revision 3 deferred
`verifier.floor` to v2 on the grounds that "a floor on a value that cannot move in static v1 is a
knob with no reachable effect." **That argument was wrong, and the answer exposes why:** the judge's
tier *does* vary in static v1 — it varies **across tasks** (it tracks each task's actor), just not
across attempts of one task. A plan with `easy` tasks resolves `easy` judges, and *"never verify
anything with less than a `medium` judge, however trivial the task looked"* is a perfectly reachable
policy in a purely static run. Only the *escalation-driven* movement (charter Decision 6's "rides up
with escalation") waits for v2 — and the floor constrains that too, with no second mechanism.

**One floor, not two (reconciling charter Decisions 4 and 6).** Decision 6 already said the judge
"never drops below a configured verifier floor" without naming the knob; Decision 4 named a knob
without calling it a floor. **They are the same thing, and `tiering.verifier.minTier` is it.** There
is exactly one verifier floor concept in this design, it is v1, and in v2 it additionally bounds the
re-resolution as the actor graduates. *(Vocabulary warning, since this document now says "floor"
about two different things: the **costly floor** is a floor on harness **autonomy** — which models
it may choose, §6.2 — while the **verifier floor** is a floor on the judge's **tier** — how weak the
judge may be. They constrain different axes and never contend.)*

**A pin bypasses the floor; the advisory catches it anyway.** A judge's frontmatter `runner` pin
names a block directly, so there is no rung for the floor to raise — consistent with §6.1, where an
explicit pin bypasses resolution on the actor side too. That does mean the floor is bypassable
per-judge, and it should be: the human who pinned it said what they wanted. **But the safety
property does not depend on the floor** — the #229 finding and the preflight/JIT advisories compare
the judge's *actual strength* against the actor's, however that route was reached. **The floor
governs resolution; the advisory governs reality.** A pin can opt out of the former and never the
latter.

**When the floor cannot be met without a costly model, it DEGRADES — it does not escalate, and it
is not an error.** Ruling 3 (§6.2) forbids the harness from auto-selecting a `costly: true` block
for any reason, and a verifier floor is not an exception. So if `Candidates(minTier)` is empty
because every block serving that rung is costly, the judge **stays at the best non-costly result
from steps 2–3** and the standard §6.5 step-5 advisory fires. It does **not** climb to a stronger
rung (that spends more to satisfy a preference), and it does **not** reach the costly block.

**No new diagnostic code, and that is a deliberate refusal.** An unsatisfiable *actor* tier is
**GR2048, an error**; an unsatisfiable *verifier* floor is **an advisory line**. Same asymmetry as
D26, for the same reason: a GR code is a thing that can fail a build, and §12.6 states that no
verifier condition may ever fail one. The condition is not unreported — the **startup preflight**
surfaces it before the run, which is precisely the job that boundary exists for. This is the case
§12.6's "resisting the urge to give it a code is the design" was written for.

**Gating (Invariant 7).** The entire verifier half — floor included — is inert when tiering is
unconfigured; see invariant 7 for why that is load-bearing rather than tidy.

**Journal.** The judge's resolved route is recorded per attempt alongside the actor's (§9.3), so
#230-lite's per-tier spend line shows **what verification actually cost** — which is the number
that will decide whether a bumped judge is worth it. When the floor raised a judge, `judge.tierSource`
records `"floor"` so the cost of the policy is attributable to the policy.

**Deferred to v2 with the ladder (charter Decision 6):** re-resolving the judge **upward when the
actor graduates** (§7). *Neither the JIT re-check nor the floor is deferred — both are v1; v2 gives
them a moving actor to act on, not a new mechanism.*

## 7. The escalation ladder (#228)  [v2 — deferred]

> **DEFERRED TO v2 (named bet, §10). Retained as the ratified design for when v2 builds it — NOT
> in v1.** v1 has no ladder: a guardrail-failed tagged task simply retries *at the same tier* (the
> static resolver yields the same block) until its budget is exhausted, then halts honestly to
> needs-human for a human re-tag or pin. That is correctness-complete because the gate — not the
> model — certifies. The ladder is a *convenience* that would spend a stronger model automatically
> on the mis-tagged minority; #230-lite's measurement is what should decide whether it is worth
> building. **Deferring it also retires the DA pass's BLOCKER (F1) and the OD-A sign-off from the
> v1 critical path** (see the open-items note at the end of this section).

A deterministic retry policy — the same family as #94's maxTurns escalation, and like it, part
of the **deterministic floor**, not an overwatcher judgment (§9.2).

- **Trigger (D15):** a budget-consuming logic failure — `guardrail-failed`, `action-failed`,
  `invalid-fragment` (and a write-scope violation, which is guardrail-class) — escalates the
  next attempt's rung by one *served* rung. The budget-exhaustion outcomes `timeout` /
  `max-turns` / `output-cap` keep their tier on first occurrence (their shipped escalators —
  longer clock, more turns, split-the-write feedback — get one same-tier chance) and escalate
  the rung on a repeat. `transient`/rate-limit pauses never escalate (not failures; no budget
  consumed). A `needsHuman` signal short-circuits as today (no retry, no ladder).
- **Budget (D5 — the #201/#228 open question, RESOLVED): an escalated attempt draws from the
  SAME retry pool. No reset.** Rationale: a reset multiplies the worst case by ladder height
  (retries × rungs) — unbounded cost growth and a needs-human that arrives attempts later
  than the human configured; `retries` must keep meaning "total tries after the first". The
  sanctioned mechanism for "this task deserves MORE attempts now that it's on a stronger
  model" already exists: an overwatcher budget grant (§9.2), bounded by
  `MaxCumulativeGrantedRetries` and `maxCostUsd`, gated by `autonomyPolicy`.
- **Last-attempt guarantee (OD-A — DEFERRED to v2, unresolved):** the intent was that the final
  budgeted attempt always resolves at the **strongest served rung**, so a task never exhausts its
  budget without the strongest model getting one shot. The DA pass found this wording is a
  **BLOCKER as written (F1):** with `retries: 0` the first attempt *is* the final budgeted attempt,
  so *every* task would resolve at the strongest rung on attempt 1 and the cost thesis inverts;
  and it contradicts the D15 same-tier retry grant at the budget edge. If v2 builds the ladder,
  OD-A must be re-scoped (never fires on attempt 1; never overrides a granted same-tier retry) and
  re-presented for sign-off — informed by #230-lite data on how often tasks actually fail. **This
  is exactly the sign-off that deferral takes off the v1 critical path.**
- **Cap + composition:** the ladder tops out at the strongest *served* rung (never invents
  one); at the top, retries continue at the top until budget exhaustion → the normal
  needs-human path, unchanged. Before escalating INTO a rung, the target's probe state is
  consulted **for visibility** (logged + provenance), but per D8 it does not veto.
- **The costly floor binds the ladder absolutely (D22, charter Decision 3).** The ladder climbs
  through `Candidates(R)` (§6.2), which excludes `costly: true` blocks — so **the ladder can never
  climb into a costly model**, and "strongest served rung" means *strongest rung with a non-costly
  candidate*. A task's escalation therefore stops below a reserved frontier model rather than
  reaching it, and then exhausts its budget and halts honestly. This is the direct answer to review
  comment 7's *"I don't want re-attempts to reach for Fable at all"*: with `costly: true` the
  guarantee is structural, not a convention — **and it holds for OD-A's final-attempt jump too**,
  which is the specific leak the devil's-advocate pass identified (its finding 7, item 3). The
  proposed `routing.escalationTarget: false` field should be re-examined before it is built:
  `costly` may already subsume it (§4.2).
- **Scope:** per-task only; sibling resolutions and `defaultTier` are unaffected. **Actions
  only** — a judge guardrail is never escalated (a guardrail failure indicts the *work*, not
  the judge; the retry re-runs the action). Judge guardrails still get tier *resolution*
  (§6.1) — just no ladder.
- **State (invariant 2):** the current rung is derived: base tier + journaled attempt
  outcomes. Resume recomputes it from `run.json`; nothing new is persisted beyond the
  per-attempt provenance (`tierSource: "escalated"`), which also gives #198/#230 the visible
  "task X escalated local → frontier on attempt 3" line.

**v2 open items folded into this deferral (decide when/if v2 builds the ladder, with #230-lite
data in hand — NOT v1 sign-offs):**

- **OD-A re-scope (DA F1):** the last-attempt-at-strongest guarantee must never fire on attempt 1
  and never override D15's granted same-tier retry — or be dropped for plain +1-per-failure.
- **D15 trigger set (DA F5):** `action-failed` conflates infrastructure faults with capability;
  the refinement is to escalate only on `guardrail-failed` (the one outcome that indicts model
  capability) and give `action-failed` one same-tier retry.
- **`routing.escalationTarget: false` (DA comment 7):** the field that expresses "serves a tier on
  first attempt but never *receives* a ladder escalation or the OD-A jump." It is only meaningful
  once the ladder exists, so it is a **v2** schema field — in v1 the omit-`routing` reservation
  (§4) already fences a reserved model out of *all* resolver selection.

## 8. Steering + threshold prompts (#231)  [v2 — deferred]

> **DEFERRED TO v2 (named bet, §10). Retained as the ratified design — NOT in v1.** v1 has no
> `--prefer` flag and no threshold prompts. A v1 operator steers by editing `guardrails.json`
> (a block's `routing.tiers`) before the run — deliberate and deterministic. Deferring this
> **removes the DA route-down contradiction (F2) from the v1 critical path**: §6.2's
> never-weaker floor holds with no mid-run downward lever to contradict. The v2 build must fold
> in the DA findings noted at the end of this section.

### 8.1 Ambient steering is structured, not prose (D12)  [v2 — deferred]

v1 ambient steering is **`guardrails run --prefer <blockName|kind>`** (repeatable): candidates
matching a preference sort first *within the served-tier constraint* (§6.2 still holds — a
`--prefer local` run serves `hard` from frontier if no local block declares `hard`; leaning
harder than that is a config edit or an explicit pin, both deliberate). Free-text steering
("lean hard on local right now") requires an LLM to interpret it into routing effects —
invariant 1 says no; it is a **v2 bet** (§10) that would compile prose into this same
structured surface. The epic's intent survives: the human authors `routing.tiers`/`notes`
once, then steers with one flag or a threshold-prompt answer.

### 8.2 Threshold prompts — the `routing` autonomy boundary (D10)  [v2 — deferred]

`/plan-breakdown`-time and mid-run threshold checks are **decision boundaries governed by the
shared §2.1 `autonomyPolicy`** — no new knob (#274 reuse, exactly as #269 did). A new
`boundary: "routing"` joins `drift | wave | task` in `decisions[]`.

- **Trigger:** a probed provider at/above `tiering.thresholdPercent` (default **80**) whose
  blocks serve upcoming work, evaluated at attempt-launch boundaries (like the `maxCostUsd`
  gate — never interrupting an in-flight attempt); fires at most once per provider per run.
  The "will remaining work blow the limit" projection = remaining prompt tasks × the run's
  per-tier average attempt cost so far (rough by design; advisory only).
- **Options presented (deterministically generated):** keep current routing / `--prefer`-style
  re-bias toward each serviceable alternative / halt. At `/plan-breakdown` time the skill
  reads `guardrails providers status` and asks before finalizing tags.
- **Policy mapping:** `prompt` + TTY → real interactive choice; **`prompt` + non-interactive →
  proceed with UNCHANGED routing** + a loud log + a `decisions[]` entry (`auto-applied`,
  headline "default: routing unchanged (non-interactive)") — **not** an exit-2 halt. This is
  a deliberate, narrow carve-out from §2.1's "non-interactive prompt halts" discipline,
  justified because the status-quo default *applies nothing* (§2.1's invariant protects
  SPEND/APPLICATION of an action; declining to change routing needs no sanction, and halting
  an overnight run at "Claude hit 80%" would defeat #189's ride-it-out objective — the run
  stays bounded by `maxCostUsd` + `transientPauseBudgetSeconds` regardless). **OD-B** for
  sign-off. `halt` → genuinely halt at the threshold (the conservative user's choice).
  `auto` → apply the deterministic recommendation (prefer the highest-headroom serviceable
  alternative) with no prompt, recorded as `auto-applied`.
- A mid-run interactive answer supersedes `--prefer` for the rest of the run (D9).

**v2 open items folded into this deferral (decide when/if v2 builds steering):**

- **OD-B (routing-boundary unattended default):** the non-interactive `prompt` = proceed-with-
  status-quo carve-out from §2.1 moves to v2 with the threshold prompt it governs.
- **Route-down lever (DA F2):** the threshold prompt should offer a **human-only** "serve tier X
  from block Y for the remainder of this run" option (interactive-TTY only, never `auto`,
  recorded in `decisions[]`) — or explicitly state that halt-and-edit-config is the only
  downward path. v1 already takes the latter (§6.2); v2 decides whether to add the former.
- **Plan-scoped firing (DA F6):** a threshold prompt fires **only when ≥1 remaining task will
  actually resolve through routing** — the plan-scoped activation gate (§4) applies to probes
  and prompts too, so a legacy zero-tag run against a routing-enabled config never prompts.
- **`maxCostUsd` overshoot disclosure (DA F7):** the cap bounds *launches*, not the *last
  launch's size* (§9.1); the v2 ladder concentrates spend in the final attempt the cap cannot
  stop, so this disclosure matters more once the ladder exists.

## 9. Reconciliations

### 9.1 `maxCostUsd` (§2) — unchanged supremacy  [v1]

Tiering changes *which* attempts spend, never *how spend is governed*: every attempt's
`costUsd` + `overheadCostUsd` still charge the one cap, which still gates new launches only. No
contract change. **Disclosure (DA F7):** the cap bounds *launches*, not the *size of the launch it
lets through* — an attempt launched at $9.98 of a $10 cap runs to completion. This is pre-existing
behavior; it is called out here because the v2 ladder would concentrate spend in a pricier final
attempt, raising the expected overshoot. *(In v2, an interactive #231 decision can never raise
`maxCostUsd` — only config/CLI can, before the run.)*

### 9.2 Overwatcher (#269) — one owner for tier movement (D16)  [v2 — deferred with the ladder]

**v1 note:** with no ladder in v1 there is no automatic tier movement, so there is nothing for
the overwatcher to contend with — the overwatcher's existing levers (guidance injection, budget
grants) operate unchanged, and no attempt's tier ever changes. The reconciliation below applies
only once v2 builds the ladder.

Both react to repeated guardrail failure; they must not fight. **The ladder owns tier
movement; the overwatcher never selects models or tiers.** Ordering per attempt: the ladder's
next-rung resolution is computed deterministically FIRST; an overwatcher consult (if
triggered) receives the already-escalated planned resolution in its context and may layer its
existing sanctioned levers (guidance injection, budget grants — including the D5 "more
attempts on the stronger model" grant) on top. The ladder is floor policy (like #94), so it
fires under every `autonomyPolicy` value and even when the overwatcher is absent. A
"pin/adjust this task's tier" overwatcher fix-op is a conceivable **v2** allowlist extension
(a runtime override touching no authored file) — explicitly out of v1.

### 9.3 Journal / provenance (#198, #230-lite) — does NOT block on #349  [v1]

**Sequencing with #349 — and the fallback revision 3 had to add.** #349 (pilot-seat model
provenance — the `resolvedModel` / `effort` journal fields) was expected to land first and carry
the provenance base. **It is still OPEN** (verified at authoring time; `resolvedModel` does not
appear in the SSOT), so revision 2's "trimmed to what is additive over #349's base" was a
dependency on work that has not happened. **Ruling: Stage 2 does not block on #349.** Stage 2
lands whichever of `resolvedModel` / `effort` is not already present, in the shape #349 specifies,
and #349 then becomes a no-op for those fields. Stage 2's acceptance must therefore assert the
*end state* of the provenance object, not a delta against an unlanded change. On top of that base,
per-attempt `provenance` gains **`runner`** (resolved block name), **`kind`**, **`tier`** (the rung
that resolved), and **`tierSource`** (`task | plan-default | override` in v1; `escalated` is added
by the v2 ladder); plus an optional per-attempt **`usage { inputTokens, outputTokens }`** so a
costless local provider still shows volume for #230-lite. Absent-not-null throughout; old journals
read fine.

**Judge provenance (§6.5).** When a judge guardrail resolved through routing, its attempt record
carries a parallel **`judge { runner, kind, model, effort, tier, strength, bumped }`** object —
`bumped: true` when the weak-actor strength bump fired, and absent entirely when no judge resolved
through routing (Invariant 7). This is what makes the verifier half *measurable* rather than
merely asserted: #230-lite can then report the actor/verifier spend split, and the question "is a
bumped judge worth what it costs" becomes a number instead of an argument.

**#230-lite (v1) — the measurement that decides the v2 bets.** The run summary gains a **per-tier
spend line** — pure aggregation over the provenance above ("hard: 42k tok / $3.12 · easy: 180k
tok / $0"), degrading to tokens-only where no cost was reported. This is the single most important
v1 deliverable *after* the routing itself: it is the evidence base for whether the deferred
subsystems (probes, ladder, steering) are ever worth building.

**Invariant-7 rule:** on a **tiering-inactive run** (no task resolved through routing) the summary
prints **exactly today's cost line** — **no per-tier section and no `untiered:` bucket**. The
per-tier breakdown appears only when ≥1 attempt actually resolved through routing.

### 9.4 Definition drift (§7.2)  [v1]

`action.tier`/`action.effort` live in `task.json`, which `TaskDefinitionHash` covers whole —
so editing a tier on an already-`succeeded` task flags drift. Accepted (D17): carving
execution-hint fields out of the hash buys ergonomics at the cost of a second hashing rule and
a "which fields are hints" argument forever; the safe-suffix auto-resolve (`autonomyPolicy`)
already makes the halt cheap to clear. KISS.

### 9.5 Multi-wave plans (§14)  [v1]

Tier fields ride inside `task.json`/frontmatter, so waved plans get tiering for free
(wave-qualified identity untouched). `tiering` config is plan-level (the root
`guardrails.json`), like `promptRunners`.

## 10. Phasing and dependency order

**v1 — static routing (the two stages that ship):**

| Stage | Contents | Depends on |
|---|---|---|
| **Stage 1** (`model-tiering-foundation.md`) | #224 registry (`kind`/`effort`/`routing` + **the three model axes `costly`/`strength`/`specialization`, §4.1** + GR2043–GR2053 validation + sentinel update; non-routable-default warning §4.2) ∥ **`guardrails providers init` registry generation (§4.3)** ∥ #225 **gated** tagging (`action.tier`, frontmatter `tier`, `tiering.defaultTier`, `tiering.verifier.minTier`, skill doctrine — writes nothing when tiering unconfigured, §5) | this DoR reviewed |
| **Stage 2** (`model-tiering-consumers.md`, static subset) | #226-**static** resolver (§6.1 precedence incl. `action.runner`/`action.effort`, §6.2 candidate selection **+ the costly floor**, §6.3 unavailability→#115, **§6.5 the verifier route + BOTH boundaries — startup preflight AND per-attempt JIT re-check**, `no-route`, provenance fields §9.3 **incl. `judge`**) ∥ #229 review check **+ the judge-weaker-than-actor / equal-and-weak findings** ∥ #230-**lite** per-tier spend line | Stage 1 |
| **#223** (standalone) | `openai-compat` runner class filling the §4.4 seam | Stage 1 (the `kind` seam) + real local endpoint available |

**v2 — named bets (deferred; revisited with #230-lite measurement in hand):**

| Bet | Contents | Gating decision |
|---|---|---|
| **#227 probes** | per-`kind` `IProviderProbe` + cache (TTL doubling) + `guardrails providers status` (GR2054) + probe-advise ranking (§6.4) | OD-C (stable Claude usage surface feasible?) |
| **#228 ladder** | escalation ladder (§7) + `tierSource: "escalated"` + `routing.escalationTarget` field | OD-A re-scope (DA F1); D15 trigger set (DA F5) |
| **#231 steering** | `--prefer` + threshold prompts (`routing` autonomy boundary, §8) | OD-B (unattended default); route-down lever (DA F2) |
| **pre-existing v2 bets** | prose-steering compiler → `--prefer`; per-model $ pricing table (until then: tokens-only); overwatcher tier-pin fix-op; probe-informed *scheduling* | — |

Each stage lands its own §12 SSOT deltas + `guardrails-domain-knowledge` updates in the same
change (invariant 4).

**Open question for the maintainer's #106 review — where does #229 belong?** This revision keeps
#229 (the guardrails-review model-appropriateness check) **in v1** because it is advisory-only,
cheap, and is the tag-quality net that makes the static story work (it catches a mis-tag *before*
a run instead of relying on a v2 ladder to recover *during* one). It is not in the maintainer's
explicit KEEP list (which named #224/#225/#226-static/#230-lite), so flagging it as a decision to
confirm.

## 11. Open decisions for human sign-off

**v1 sign-offs (the only ones that gate v1):**

- **OD-D — author the rollout as a #254 waved plan (§2.1).** Recommended (dogfoods waves;
  matches the barrier shape); maintainer's call at breakdown time.
- **#229 placement (§10).** Confirm #229 (review appropriateness check) stays in v1 (this
  revision keeps it as the tag-quality net); it was not in the explicit KEEP list.

**SETTLED on 2026-08-12 — recorded here so they are not re-opened:**

- **The charter's disposition-2 consequence (§6.5 step 5) — CONFIRMED.** When "judge ≥ actor"
  cannot be met without a costly model the verifier rule *degrades to an advisory rather than
  overspending*; **the actor route does the opposite — it halts** (§6.2, invariant 5). Degrade what
  is advisory; halt what is load-bearing.
- **Charter Decision 9 ("both boundaries") is v1 in full — the JIT re-check is NOT deferred.** An
  earlier draft proposed deferring the per-attempt half because static v1 has nothing that
  graduates; **overruled.** §6.5 states plainly what the second boundary buys without graduation
  (the preflight models the resolver, the JIT check *is* the resolver; it is the only boundary that
  sees a resume-time config edit or an overwatcher change; §9.3 already pays for the data) and
  carries the de-duplication rule that keeps three surfaces from shouting one finding.
- **The provider-kind fallback is verifier-only (D21a) — RATIFIED.** §4.1.
- **OD-G — how sharp the `costly` cliff is — ANSWERED: never automatic, full stop.** A `costly:
  true` block is never auto-selected at any rung, by the resolver, the judge bump, or the v2 ladder.
  A config whose only `hard`-capable block is `costly` therefore makes `hard` unservable, and that
  is a **validate-time ERROR (GR2048)** — not a warning to route around, and not a case for a
  softer "expensive but routable" setting. The axis is **not** split into `costly`-for-accounting
  plus `reserved`-for-the-floor. §4.2, §6.2, §14 (the exact error text).
- **OD-E — `providers init` enumeration for Claude — ANSWERED: degrade honestly.** Chosen over
  shipping a curated model list and over failing the command. Now a **hard rule** with its reason
  stated: a model id may only come from a provider that reported it or a human who typed it, because
  a registry entry is a **routing target**, and a fabricated id would be spent against at a model
  that may not exist. §4.3 ruling 2.
- **OD-F — retiring `routing.rank` — ANSWERED: drop it.** Candidates order by **ascending
  `strength`** — *the weakest model that can serve the tier goes first* — and "this model should not
  serve that tier" is expressed by editing its `routing.tiers`. New warning **GR2046** fires on a
  leftover `rank` key so a migrated config's ordering can never change silently. §4.2, §13.
- **OD-H — the plan-wide verifier key — ANSWERED, and it CHANGED the design: it is a FLOOR, not a
  default.** `tiering.verifier.minTier` never *selects* the judge's rung (the actor's-rung-plus-bump
  rule still does); it only refuses a result that came out below it, and never lowers one. This also
  **collapses charter Decisions 4 and 6 into one floor concept**, moves the floor from v2 into v1,
  and degrades to an advisory — never an error, never a costly auto-selection — when it cannot be
  met. **§6.5.1** is the full ruling, including the rename rationale.

**Nothing is open in the charter.** All eight `:::question` blocks in
[`model-tiering-verifier.charter.md`](model-tiering-verifier.charter.md) now carry answers; the
blocks remain as the durable record. §11 above is the canonical statement of each outcome, and
**if the two ever disagree, the charter is what was answered, so the charter's wording wins.**

**Deferred to v2 — decide with #230-lite dogfood measurement in hand, when/if v2 builds the
subsystem each one gates. These are NOT open v1 sign-offs.**

- **OD-A — last-attempt-at-strongest guarantee (§7, #228 ladder).** Deferred with the ladder.
  The DA pass showed the current wording is a BLOCKER (F1: `retries: 0` routes everything to
  frontier); if v2 builds the ladder, re-scope (never on attempt 1; never override a granted
  same-tier retry) or drop for plain +1-per-failure — informed by how often #230-lite shows
  tasks actually failing.
- **OD-B — routing-boundary unattended default (§8.2, #231 steering).** Deferred with the
  threshold prompt it governs.
- **OD-C — Claude usage-probe feasibility (§6.4, #227 probes).** Deferred with the probes; this
  feasibility risk is *why* probes are a v2 bet rather than a v1 phase.
- **Route-down lever (DA F2) and D15 trigger set (DA F5).** Deferred with #231 / #228
  respectively (§8, §7).

## 12. Proposed SSOT deltas (verbatim-appliable at build time — the live SSOT is NOT touched by this PR)

> **§12 is now split: §12.1/§12.3/§12.4/§12.5/§12.6 are the v1 deltas that LAND (Stage 1/2);
> §12.7 collects the v2-deferred deltas (probes/ladder/steering) so they are not accidentally
> shipped in v1.** Only the static-routing schema lands in Stage 1.

### 12.1 §2 `guardrails.json` — Stage 1 [v1]

Add a top-level optional block (after `preserveAttemptsForSalvage`). **In v1 the `tiering` block
holds exactly TWO keys — `defaultTier` and `verifier.minTier`;** the `thresholdPercent` /
`probeCacheSeconds` knobs are v2 (they configure probes and threshold prompts — see §12.7).

```jsonc
  "tiering": {                        // OPTIONAL (#201). Tiering is CONFIGURED iff >=1 runner block declares
                                      //   `routing` (below); ACTIVE for a task only when it resolves through
                                      //   routing (§4). Absent = none.
    "defaultTier": "medium",          // OPTIONAL plan-wide tier for UNTAGGED prompt actions: "easy"|"medium"|"hard"
                                      //   (GR2043 if unrecognized). EXAMPLE value — there is NO built-in default;
                                      //   absent = an untagged task keeps LEGACY resolution (§5).
    "verifier": {                     // OPTIONAL (#201 verifier half, §6.5). The judge's rung is ALWAYS chosen by
                                      //   the rule: the ACTOR's rung, bumped one STRENGTH rank when the actor is
                                      //   weak. Inert when tiering is unconfigured.
      "minTier": null                 // OPTIONAL plan-wide FLOOR (§6.5.1): the resolved judge may never end up
                                      //   BELOW this rung. It never SELECTS a rung — it only refuses one that came
                                      //   out too low, and never lowers a result. "easy"|"medium"|"hard" (GR2043).
                                      //   Unsatisfiable without a costly block => the judge stays put + an ADVISORY
                                      //   (never an error, never a costly auto-selection — §6.5.1).
    }
  },
```

Inside the canonical `promptRunners` block (and **byte-for-byte in
`.claude/skills/plan-breakdown/references/schemas.md` between its
`canonical-schema:promptRunners` sentinels** — drift-tested), add to the `"claude"` example
block after `"model": null,`:

```jsonc
      "kind": "claude",               // OPTIONAL provider discriminator (#224); DEFAULT "claude" (back-compat).
                                      //   v1 implements "claude"; "openai-compat" is the reserved #223 seam
                                      //   (Ollama/llama.cpp/LM Studio/vLLM); "codex"/"openrouter" reserved.
                                      //   Unrecognized OR not-yet-implemented kind = GR2044 (never a silent
                                      //   fallback to claude).
      "effort": null,                 // OPTIONAL thinking-effort knob (#201); OPAQUE string, shape-checked like
                                      //   `model` (GR2050), TRANSLATED by the runner CLASS (spelling quarantined
                                      //   there, like maxOutputTokens). Same model at two efforts = two blocks.
      "costly": false,                // OPTIONAL (#201, §4.1). TRUE = the harness may NEVER auto-select this block:
                                      //   not for its rung, not for a stronger-rung climb, not for a v2 ladder
                                      //   escalation, not for a judge bump. Reachable ONLY by an explicit task pin
                                      //   (action.runner/action.model) or as the `default` pointer (warned, GR2051).
                                      //   TRI-STATE, settled 2026-08-15 — this DoR previously said "absent =
                                      //   false" and the maintainer OVERRULED it in favour of what Stage 1
                                      //   shipped: absent is NULL ("not stated"), distinct from an explicit
                                      //   `false` ("stated cheap"). Three states in the SCHEMA; TWO at the
                                      //   candidacy PREDICATE — null behaves as NOT-costly there, because an
                                      //   un-annotated registry must stay routable (§6.2). The distinction is
                                      //   what `providers init` exists to close: it can name every block whose
                                      //   cost is unstated and ask, which "absent = false" made unaskable by
                                      //   silently answering for the user. Declaring `routing` alongside a
                                      //   costly block is inert (GR2052).
      "strength": null,               // OPTIONAL (#201, §4.1). Integer >= 1; HIGHER = stronger. The ONLY totally
                                      //   ordered axis, and the only one a >= comparison or a bump may read.
                                      //   Orders same-rung candidates ASCENDING (weakest sufficient first);
                                      //   absent/unspecified sorts LAST for ordering, and counts as WEAK for the
                                      //   §6.5 verifier comparison (provider-kind fallback). Malformed = GR2045.
      "specialization": null,         // OPTIONAL (#201, §4.1). One of "coding"|"planning-reasoning"|"general"|
                                      //   "unspecified" (absent = "unspecified"). A PREFERENCE, never an ordering:
                                      //   it breaks ties among candidates already meeting the required strength
                                      //   (§6.5) and can neither satisfy nor violate >=. Outside the enum = GR2045.
      "routing": {                    // OPTIONAL (#224): opts this block into tier resolution (§9.6). Absent =
                                      //   block reachable only explicitly / as default (today's behavior).
        "tiers": ["medium", "hard"],  // REQUIRED here; non-empty subset of easy|medium|hard — which rungs this
                                      //   (kind, model, effort) route may serve. Malformed = GR2047.
        "notes": "…"                  // OPTIONAL human-authored prose guidance; surfaced to humans and MAY be
                                      //   appended to composed prompts as context — NEVER parsed for routing.
      },
```

*(Ordering among same-rung candidates comes from `strength`, not from a `routing.rank` — D25/OD-F,
§4.2. The comment text above is what `guardrails providers init` (§4.3) emits into the user's own
`guardrails.json`, which is why the legal values are spelled out inline rather than only here.)*

Prose bullets to add under §2: the **configured-vs-active** activation rule (configured iff ≥1
`routing` block; *active for a task* only when it has an effective tier AND a serving block —
plan-scoped, §4); tags without any routing block = **GR2049 warning**, plan runs as today; the
GR2048 rule (§12.5); the reserved-model warning (a `costly` **or** `routing`-less block must not be
`default`, §4.2); **the single candidacy predicate** (`routing` present ∧ rung ∈ `tiers` ∧ not
`costly`) stated ONCE and referenced by the resolver, GR2048, and `no-route` (§6.2); and **the
costly floor in one sentence** — *the harness never auto-selects a `costly: true` block; only an
explicit task pin or the `default` pointer reaches one.*

### 12.2 §2.1 `autonomyPolicy` — [v2 — deferred with #231; consolidated in §12.7]

**Does NOT land in v1** (there are no threshold prompts in v1). Retained as the ratified delta:

- `boundary` enum: `drift` (#274) | `wave` (#254) | `task` (#269) | **`routing` (#231 —
  provider-limit threshold decisions, §9.6)**.
- Add: "**Routing-boundary carve-out (#231):** at a `routing` boundary the non-interactive
  `prompt` default is *apply nothing* — proceed with unchanged routing, loudly logged and
  recorded as `auto-applied` ('default: routing unchanged') — not an exit-2 halt, because the
  status-quo default applies/spends nothing (the invariant guards APPLICATION; declining to
  change routing needs no sanction, and the run remains bounded by `maxCostUsd` +
  `transientPauseBudgetSeconds`). `halt` still halts at the threshold; `auto` applies the
  deterministic highest-headroom recommendation."

### 12.3 §3 `task.json` — Stage 1 [v1]

In the `action` block after `"model": null,`:

```jsonc
    "tier": null,                    // prompt actions only (#225): "easy"|"medium"|"hard" difficulty tag feeding
                                     //   attempt-launch tier resolution (§9.6); GR2043 if unrecognized. null/absent
                                     //   = inherit tiering.defaultTier (§2), else legacy resolution.
    "effort": null,                  // prompt actions only (#201): per-task thinking-effort override; mirrors
                                     //   `model`'s SHAPE (GR2050 shape check; opaque to the harness) but NOT its
                                     //   bypass — with a tier but no full pin, resolution still selects the block
                                     //   and `effort` overrides the resolved route's effort (§6.1 item 2).
```

Replace the `action.model` resolution-order sentence with: "**Full pin — `action.runner` or
`action.model`** (if set — bypasses tier resolution entirely) **> tier resolution (§9.6, when an
effective tier exists and a block serves it; `action.effort` alone overrides the resolved route's
effort without bypassing) > `promptRunners.<name>.model` > the CLI's own default.**" Add: a
`validate` **warning** when a full pin and `action.tier` coexist on the same action (the tier is
dead weight). Also SSOT §4.2: frontmatter gains the optional `tier` key (judge guardrails; resolution
applies).

### 12.4 §7 journal — Stage 2 [v1] (does NOT block on #349 — §9.3)

- `provenance` gains additive optional fields. **#349 is still open, so Stage 2 lands whichever of
  `resolvedModel` / `effort` is not yet present, in #349's shape** (§9.3), plus:
  `"runner"` (resolved block name), `"kind"`, `"tier"` (the rung that resolved), `"tierSource"`:
  `"task" | "plan-default" | "override"` (the `"escalated"` value is added by the v2 ladder —
  §12.7). Absent (never null noise) for script attempts / legacy journals.
- Attempt record gains optional `"usage": { "inputTokens": 0, "outputTokens": 0 }` (additive; the
  tokens-only accounting surface for costless providers, #230-lite — unless #349 already carries
  it).
- Attempt record gains optional **`"judge": { "runner", "kind", "model", "effort", "tier",
  "strength", "bumped" }`** — the verifier route that graded this attempt (§6.5). Absent entirely
  when no judge resolved through routing (Invariant 7); `"bumped": true` when the weak-actor
  strength bump fired.
- Attempt `outcome` enum gains **`no-route`** — resolution found zero registered candidate blocks
  at-or-above the task's rung (a runtime config gap; validation GR2048 normally prevents it).
  Settles needs-human with "register a provider serving tier ≥ R" feedback. This is a v1 defensive
  outcome independent of probes (§6.2).
- *(v2, §12.7: `decisions[]` `boundary` gains `routing`.)*

### 12.5 §9 — seam note + a new §9.6 "Tier routing (model tiering, #201)" [v1]

§9 intro: note that `FromConfig` switches on `kind` (GR2044 gate) and that `--model`/effort flags
are emitted from the RESOLVED route. New **§9.6 (v1 content)** documenting, normatively: the
three model axes and their unspecified-fallbacks (§4.1); **the single candidacy predicate and the
costly floor** (§6.2); the precedence chain incl. `action.runner`/`action.effort` (§6.1); candidate
selection + never-route-down + nearest-stronger-rung climb + ascending-`strength` ordering (§6.2);
the `no-route` defensive outcome; provider-unavailability → the shipped #115 pause +
never-weaker-hold (§6.3); **the verifier route, its strength bump, its advisory degradation, and
`guardrailOverrides` composing with the JUDGE's block** (§6.5); the plan-scoped
configured-vs-active activation rule (§4). (Content = this DoR's §4–§6, compressed to contract
language.) **§9.6 explicitly states there is no ladder, no probe, and no steering in v1.**

### 12.6 Validation summary [v1] (GR text in §13)

- GR2009's runner-command probe extends per-kind (an `openai-compat` block probes its endpoint
  reachability as a **warning**, mirroring the PATH probe — lands with the #223 standalone runner).
- Four new **warnings** (v1): a `costly` **or** `routing`-less block named `default` in a
  tiering-configured file (reserved-model back-door, §4.2); a `costly: true` block that also
  declares `routing` (inert, §4.2); a **full pin + `action.tier`** coexisting
  on one action (§6.1); and a retired **`routing.rank`** key (GR2046, §4.2 — so a migrated config's
  ordering never changes silently). All warnings, not errors — the plan still runs.
- **The verifier check is NOT a validation code.** A judge weaker than its actor is an *advisory*
  (charter Decision 1): a #229 review finding, a startup preflight warning **line**, and a per-attempt JIT re-check — not a
  GR-coded diagnostic and never a load-time refusal. Resisting the urge to give it a code is the
  design; a GR code is a thing that can fail a build.

### 12.7 v2-deferred deltas — do NOT ship in v1

Consolidated so a Stage-1/2 implementer can see exactly what to leave out. Each lands with its
v2 bet (§10), in the same change as its code:

- **`tiering.thresholdPercent` (default 80) + `tiering.probeCacheSeconds` (default 60, GR2054 if
  ≤0)** keys — with #231 / #227 respectively.
- **§2.1 `autonomyPolicy` `routing` boundary** + the non-interactive carve-out (§12.2) — with #231.
- **`decisions[]` `boundary: "routing"`** — with #231.
- **`provenance.tierSource` value `"escalated"`** and the **judge re-resolution on escalation**
  (`judge.bumped` recomputed per attempt, charter Decision 6) — with #228.
- **`routing.escalationTarget: false`** block field — with #228 (§4.2, §7), **and only after
  checking whether `costly` already subsumes it**.
- **GR2054** (`RoutingNumericNonPositive`) — with #227 (the only reserved code that is a v2 delta;
  GR2043–GR2053 are all v1 — §13).
- **§9.6 normative language** for probes-advise / the ladder / `--prefer` + threshold prompts /
  the ladder-first-overwatcher-layers ordering — with the respective bet.

## 13. Diagnostic codes — GR2043–GR2046 allocated by Stage 1, GR2047–GR2054 reserved (next-free marker → GR2055)

> **Revision 3 reallocated this whole block, and the reason is worth more than the numbers.**
> Revision 2 reserved **GR2037–GR2045** and said so with confidence — *"verified against
> `DiagnosticCodes.cs` at authoring time."* It was true on 2026-07-09. **By 2026-08-12 every single
> one of GR2037–GR2042 had been taken by shipped work**: GR2037 `BannedGuardrailPattern` (#346),
> GR2038 `WorktreePathTooLong` (#383), GR2039 `InvalidAutonomyDialValue` + GR2040
> `IncompatibleAutonomyCompoundConfig` (#361), GR2041 `MissingWriteScope` (#389), GR2042
> `StructuralOverScope` (#378/#382). A design doc that had sat unmerged for a month was still
> claiming them.
>
> **The lesson, which belongs in the design and not just in a changelog: a code reservation held in
> an unmerged document is not a reservation — it is a wish.** Nothing enforces it; the file is the
> only registry. So this section now carries a standing instruction rather than a promise:
>
> **Stage 1 MUST re-verify this block against `DiagnosticCodes.cs` immediately before landing, and
> renumber if it has rotted again. The file wins. The numbers below are a shape, not a claim.**
> Revision 2 wrote exactly that sentence about the epic briefing's stale "GR2036 next" and then
> made the same mistake one layer up — which is the most instructive thing in this document.
>
> ---
>
> **THIRD ROT — and this time the instruction above was the thing that got skipped.** Stage 1 has
> landed (master `5788a54`) and it did **not** re-verify against this section: it allocated its own
> numbers and its own names. The lesson is therefore no longer a cautionary tale told once — it is
> **empirically confirmed twice over**, and the second confirmation is the sharper of the two,
> because the failure mode itself changed:
>
> - **Rot #1 and #2 were SHIFTS.** A contiguous block was overtaken from below; every reserved code
>   still meant what this document said it meant, just at a different number. Renumbering was
>   arithmetic, and a careless bulk edit would still have landed on the right answer.
> - **Rot #3 is a COLLISION.** `GR2043` now exists on both sides and **names two different things**:
>   this document called it `UnsupportedRunnerKind` (the `kind` discriminator); the file ships it as
>   `InvalidTierValue` (the tier enum). `GR2045` collided in the opposite direction — this document's
>   `UnrecognizedTier` against the file's `InvalidRunnerAxis`. The two numbers effectively **swapped
>   meanings.** A find-and-replace over this document would have silently corrupted it into
>   confident, well-formatted nonsense; every one of the ~120 references had to be re-pointed at the
>   concept it was actually discussing.
>
> **The most instructive detail is inside `DiagnosticCodes.cs` itself.** Its comment above the
> provider-registry block records that GR2043 was *deliberately skipped* there while a concurrent
> action-tier change in the same Stage-1 plan claimed it — *"Taking it twice for two different
> meanings is the one outcome a code registry must not produce, and a gap costs nothing"* — followed
> by a post-merge note that the gap closed once that change landed. **So the file's own allocation
> discipline worked perfectly.** Two concurrent slices coordinated *inside* the registry and produced
> no collision at all. The only reservation that got trampled was the one held **outside** the file,
> in this document. That is this section's thesis demonstrated rather than asserted: **the registry
> defends itself; a document cannot defend a claim on the registry.** The standing instruction is not
> paperwork — it is the only mechanism this design has, and skipping it is exactly why §13 has now
> been rewritten three times.
>
> **The instruction stands, unchanged and now twice-proven, for Stage 2 and every later stage:
> re-verify against `DiagnosticCodes.cs` immediately before landing, and renumber if it has rotted
> again. The file wins.** §13.2 below is, as ever, a shape and not a claim.

Re-verified against `src/Guardrails.Core/Loading/DiagnosticCodes.cs` on **2026-08-15** (post
Stage-1 merge): **GR2046** (`RetiredRoutingRank`) is the last taken code and the file's marker line
says **GR2047 is next-free**. Four of this block's codes have therefore **already shipped** (§13.1),
under numbers — and in three of four cases names — that differ from what this document reserved. The
remaining eight are renumbered into the free range and re-reserved as **GR2047–GR2054** (§13.2);
their constants + the historical-comment discipline land at build time, per stage, and the marker is
then bumped to **GR2055**. The **Scope** column marks v1 vs a v2 bet — **GR2054 is the only code
deferred to v2.**

### 13.1 Already allocated — landed with Stage 1 (the file's names are canonical)

These are **not reservations**; they are shipped constants. The names below are the ones in
`DiagnosticCodes.cs`, **not** the names this document preferred — where the two differ, the file
wins, which is the whole point of §13. The "DoR reserved as" column exists only so that a reader of
an earlier revision, or of a review comment written against one, can find out what a stale number
now means.

| Code | Shipped name | Sev | Scope | DoR reserved as | Meaning as shipped |
|---|---|---|---|---|---|
| GR2043 | `InvalidTierValue` | error | **v1** | GR2045 `UnrecognizedTier` | a declared difficulty tier is not one of `easy`/`medium`/`hard`. Matched VERBATIM — no trimming, no case-folding — so `"hard "` is reported rather than silently accepted (the GR2030 preserve-the-malformed-signal doctrine); an absent tier is never flagged. **Narrower than reserved:** the shipped check covers **two** sites — `task.json action.tier` and the plan-wide `tiering.defaultTier` — not the four this DoR specified (§13.3) |
| GR2044 | `InvalidPromptRunnerKind` | error | **v1** | GR2043 `UnsupportedRunnerKind` | a `promptRunners.<name>.kind` is present but names no recognised runner kind; the message NAMES the offending value. The loader falls the block back to the `claude` default **only** so the rest of validation still reports, never so the run proceeds. **Narrower than reserved:** a RECOGNISED-but-unimplemented kind is *not* this code — it loads clean and fails at registry construction with an actionable message (charter §A.2 — the backstop, not the gate) |
| GR2045 | `InvalidRunnerAxis` | error | **v1** | GR2049 `MalformedModelAxis` | one of the three per-block model axes is malformed (§4.1): `costly` not a bool, `strength` not an integer or below 1, or `specialization` outside the enum. One diagnostic per malformed axis, naming the axis and its value; an absent axis is never flagged |
| GR2046 | `RetiredRoutingRank` | warning | **v1** | GR2054 `RetiredRoutingRank` | a `promptRunners.<name>.routing` block still carries the RETIRED `rank` key (§4.2, settled OD-F). The key is IGNORED. A warning rather than an error so a config mid-migration keeps loading — and rather than silence, because accepting `rank` quietly is exactly how a migrated config's ordering would change with nobody told. **Name matched; only the number moved** |

### 13.2 Still reserved — NOT yet allocated (renumbered into GR2047–GR2054)

The eight codes Stage 1 did not ship, renumbered into the free range **in their original order**.
Re-verify against the file before landing.

| Code | Name | Sev | Scope | Meaning |
|---|---|---|---|---|
| GR2047 | `MalformedRoutingGuidance` | error | **v1** | `routing` block invalid: missing/empty `tiers`, a value outside the tier enum, or wrong types |
| GR2048 | `UnservableTier` | error | **v1** | a USED tier (task tag, frontmatter tag, or a `defaultTier`) in a tiering-configured plan has no **candidate** block at-or-above it (§6.2) — either none serves it, or **the only ones that do are `costly: true`**, which the harness may never select. The message MUST distinguish the two: they have different fixes |
| GR2049 | `TieringInert` | warning | **v1** | tier tags present but NO block declares `routing` — tags have no effect; plan runs with legacy resolution |
| GR2050 | `EffortInvalid` | error | **v1** | a present `effort` fails the GR2030-style shape check (non-empty, no whitespace/control chars). **TWO sites, not three** — `promptRunners.<name>.effort` and `action.effort`. This row said "block, override, or `action.effort`" until 2026-08-15, but **`guardrailOverrides.effort` is not modelled anywhere**: §12.1's canonical block never showed it, and Stage 1.5 implemented the two sites that exist. The third was an internal inconsistency in this document, not a deferred feature — see §13.4 |
| GR2051 | `NonRoutableBlockIsDefault` | warning | **v1** | a `costly: true` **or** `routing`-less block is the registry `default` pointer in a tiering-configured file — untagged tasks would fall to a model held out of routing (§4.2; review comment 7's back door) |
| GR2052 | `CostlyBlockRoutingInert` | warning | **v1** | a `costly: true` block also declares `routing` — the routing can never apply, because the candidacy predicate excludes costly blocks (§6.2). A warning, so GR2048 can report the real consequence |
| GR2053 | `PinAndTierCoexist` | warning | **v1** | a full pin (`action.runner`/`action.model`) and `action.tier` are both set on one action — the tier is dead weight the pin overrides (§6.1, DA F3) |
| GR2054 | `RoutingNumericNonPositive` | error | **v2 (#227)** | `tiering.probeCacheSeconds` / `thresholdPercent` present but not a positive value (cf. GR2012/GR2023/GR2036) |

### 13.3 The coverage gap this renumbering exposed — recorded, not silently fixed

Re-verifying against the file surfaced something the numbers alone would have hidden: **Stage 1
shipped four of the ten v1 validation codes this DoR assigned to it** (§10's Stage 1 row, §17's
handoff). Still unimplemented: `MalformedRoutingGuidance`, `UnservableTier`, `TieringInert`,
`EffortInvalid`, `NonRoutableBlockIsDefault`, `CostlyBlockRoutingInert`, `PinAndTierCoexist`.

**Most of these are blocked on schema, not merely deferred — which matters more than the count.**
Three of the seven have **no field to validate yet**, because the fields this DoR specified did not
land with Stage 1: `routing.tiers` (the shipped `routing` block carries `guidance`/`tags` instead —
so `MalformedRoutingGuidance` has no `tiers` to check, and §6.2's candidacy predicate has no tier
field to read at all), `effort` at either site (so `EffortInvalid` has no target), and
`tiering.verifier.minTier` (§6.5.1). The gap is therefore a **contract** gap first and a diagnostic
gap second. Of the rest, the most load-bearing absence is **GR2048 `UnservableTier`** — the
validate-time error the whole `costly`-cliff ruling (§4.2, §6.2, §14.1, OD-G) rests on: until it
exists, *"hard tasks must be pinned by a human"* is a design intention with nothing enforcing it,
and Stage 2 must not assume validation has already fenced the case its resolver will meet.
Separately, shipped `GR2043 InvalidTierValue` covers two of the four tier-bearing sites this DoR
specified — a judge guardrail's frontmatter `tier` (SSOT §4.2) and `tiering.verifier.minTier`
(§6.5.1) are not merely unvalidated, they are **not parsed at all**.

**This section only records the gap; it does not re-phase the epic, and it is not the whole
Stage-1 delta.** §10 and §17 still state the DoR's intended Stage-1 scope, deliberately unedited, so
the divergence stays visible rather than being quietly absorbed. Whether the remainder is a Stage-1
follow-up or folds into Stage 2 — and how the shipped `routing` shape is reconciled with the one
§12.1 specifies — is a maintainer call at the #106 review of this document.

Historical-comment discipline for the build-time edit: "Next-free allocation **re-confirmed at
landing time** (the model-tiering DoR `docs/plans/17-model-tiering.md` reserved GR2043–GR2054 on
2026-08-12, after its ORIGINAL GR2037–GR2045 reservation was overtaken by #346, #383, #361, #389 and
#378 while the design sat in draft; Stage 1 then allocated GR2043–GR2046 on its own numbering
without re-verifying, so the remaining eight were re-reserved at GR2047–GR2054 on 2026-08-15)."
**v1 (static routing + verifier)** takes **GR2043–GR2053** (#224/#225/#226/#201-verifier) across
Stages 1–2 — of which GR2043–GR2046 have shipped; **v2** takes **GR2054** with #227's probes.
CURRENT next-free code: **GR2047** until the §13.2 block lands, **GR2055** once it has.

### 13.4 `guardrailOverrides.effort` — a phantom third site, retired 2026-08-15

§13.2's GR2050 row claimed `effort` had **three** sites: the block, an override, and `action.effort`.
Stage 1.5 implemented **two**, because the third does not exist and never did:

- `guardrailOverrides` is a partial override of a runner profile (permissions / tools / turns), and
  **no `effort` key is modelled on it** — not in `RawPromptRunnerOverrides`, not in the resolved model.
- §12.1's canonical block — the authority this document points generators and hand-editors at — has
  **never shown** `guardrailOverrides.effort` either.

So the row was internally inconsistent with the schema in the same document. It is corrected rather
than deferred: **there is no missing feature here, only a sentence that named a key nothing declares.**

**If a judge ever needs its own effort**, that is a real design question and belongs with the verifier
route (§6.5) — a judge already resolves its own `(provider, model, effort)`, so the question is
whether the *override profile* should be able to pin the effort independently of the resolved tier.
Raise it there, as a change with a rationale, rather than resurrecting it from a stale table row.

**The general lesson matches §13's:** a claim about the schema that lives outside the schema decays
without anything noticing. This one survived a full revision pass and a devil's-advocate gate because
it read as plausible — it took an implementer trying to build the third site and finding nothing to
build against.

## 14. Worked example  [v1 — static routing]

`guardrails.json` (v1 target state, as `guardrails providers init` would emit it and a human would
then annotate; until #223 lands, the `local-kimi` block fails validation with GR2044 naming
`openai-compat` — delete it to run on a claude-only box today). Note the **`fable` block**: it is
`costly: true`, so the resolver may never select it — **not at its own rung, not by a climb, not
by a v2 ladder escalation, and not as a judge bump.** That is review comment 7's *"I don't want
re-attempts to reach for Fable at all"*, made structural. It is deliberately **NOT** the `default`
pointer (that would trip GR2051 and route untagged tasks to it); `default` is `sonnet`.

```jsonc
{
  "version": 1,
  "maxCostUsd": 10.00,
  "tiering": { "defaultTier": "medium" },            // verifier{} omitted => the §6.5 rule-based default
  "promptRunners": {
    "default": "sonnet",                              // a ROUTING block, never the reserved fable block
    "fable":      { "command": "claude", "model": "claude-fable-5", "effort": "xhigh",
                    "costly": true, "strength": 5, "specialization": "planning-reasoning" },
                    // RESERVED, DECLARED: costly => the harness may never CHOOSE it; only an explicit
                    //   action.runner/action.model pin reaches it. (Reserving Fable for /plan-breakdown
                    //   itself is a session choice, outside this registry entirely — §4.2.)
    "opus":       { "command": "claude", "model": "claude-opus-4-6", "effort": "high",
                    "strength": 4, "specialization": "planning-reasoning",
                    "routing": { "tiers": ["hard"],
                                 "notes": "cross-module architecture, retry/journal contract work" } },
    "sonnet":     { "command": "claude", "model": "claude-sonnet-4-5",
                    "strength": 3, "specialization": "coding",
                    "routing": { "tiers": ["medium", "hard"],
                                 "notes": "typical single-module coding; hard fallback when opus busy" } },
    "local-kimi": { "kind": "openai-compat", "command": "http://inference.local:11434",
                    "model": "kimi-70b", "strength": 2, "specialization": "coding",
                    "routing": { "tiers": ["easy", "medium"],
                                 "notes": "mechanical refactors, doc/skill updates, migrations; free" } }
  }
}
```

Tasks: `01-author-stats-tests` (`"tier": "medium"`, carrying a **prompt-judge guardrail**),
`02-implement-stats` (`"tier": "hard"`), `03-update-docs` (untagged → defaultTier `medium`),
`04-hand-added-hotfix` (human-added, `action.model: "claude-opus-4-6"` pinned).

| Attempt | Effective rung | Candidates (asc. strength) | Actor resolves to | Judge (§6.5) | Provenance |
|---|---|---|---|---|---|
| 01 att 1 | medium | local-kimi(2), sonnet(3) | **local-kimi** / kimi-70b | actor is weak (strength 2) ⇒ bump ⇒ **sonnet** (3, weakest > 2 at `medium`) | tier=medium, tierSource=task, judge.bumped=true |
| 01 att 2 (guardrail-failed) | medium (**no ladder in v1 — same rung**) | local-kimi(2), sonnet(3) | **local-kimi** again | **sonnet** again (static ⇒ identical every attempt) | tierSource=task |
| 01 att 3 (still failing) | medium | — | budget exhausted → **needs-human** (re-tag to `hard`, or pin) | — | honest halt |
| 02 att 1 | hard | opus(4), sonnet(3) → asc: sonnet(3), opus(4) | **sonnet** | actor not weak ⇒ **judge = sonnet**, equal-and-strong, no bump, no finding | tierSource=task |
| 03 att 1 | medium (default) | local-kimi(2), sonnet(3) | **local-kimi** | bumped ⇒ **sonnet** | tierSource=plan-default |
| 04 att 1 | — (pinned) | — (bypasses resolution) | claude-opus-4-6 | actor strength from the pinned block ⇒ not weak ⇒ no bump | tierSource=override |

**Read row 02 twice — it is the D25 ordering change, visible.** `hard` is served by both `sonnet`
(3) and `opus` (4), and ascending-strength picks **sonnet**, the weakest model the human said may
serve `hard`. Under revision 2's `rank` it was `opus`. This is the token-saving thesis expressed as
an ordering rather than as a hand-maintained preference list — and the *reason* it is safe is the
same reason the whole epic is safe: if sonnet's work is not good enough, the **deterministic gate
fails it**, and the task halts for a human. If you want `hard` to mean opus, say so where it is
honest to say it: remove `hard` from sonnet's `tiers`. **(OD-F is your one-line overrule.)**

### 14.1 The `costly` cliff, and the error a user actually meets  [settled — was OD-G]

The config above works because `hard` has a non-costly candidate (`sonnet`, and `opus`). Now make
the change a cost-conscious user will reach for on day one — **mark `opus` costly too**:

```jsonc
    "opus": { "command": "claude", "model": "claude-opus-4-6", "effort": "high",
              "costly": true, "strength": 4,                    // <-- added
              "routing": { "tiers": ["hard"], "notes": "…" } },
    "sonnet": { "command": "claude", "model": "claude-sonnet-4-5", "strength": 3,
                "routing": { "tiers": ["medium"], "notes": "…" } },   // <-- no longer serves hard
```

`hard` now has **no candidate**: `opus` is excluded by the costly floor and `sonnet` no longer
declares it. `guardrails validate` fails, before a single token is spent:

```
error GR2048: task '02-implement-stats' is tagged tier 'hard', but no block can serve it.
  The only block declaring tier 'hard' is 'opus', which is marked costly: true —
  the harness never auto-selects a costly model (guardrails.json > promptRunners.opus).
  Fix ONE of:
    - pin the task explicitly:  "action": { "runner": "opus" }   (a costly model is
      reachable by YOUR assignment, just never by the harness's choice)
    - clear "costly": true on 'opus', or
    - add tier 'hard' to a non-costly block's routing.tiers
warning GR2052: promptRunners.opus declares 'routing' but is marked costly: true —
  the routing block is inert (a costly block is never a tier candidate).
```

**This is intended behavior, not a rough edge.** The config is now saying, checkably, *"hard tasks
must be pinned by a human"* — and it says so at validate time rather than by surprising you with a
bill. Note what the harness does **not** do: it does not fall back to `sonnet` (that would route
weaker than asked, §6.2), it does not "warn and use opus anyway" (that is the floor, and the floor
has no override), and it does not wait until runtime to mention it. The two diagnostics divide the
work honestly — **GR2052 explains why the block is out, GR2048 explains what that costs you** —
which is why GR2052 is a warning and GR2048 is an error rather than one combined complaint.

**Read row 01's judge column too — that is the charter, working.** The actor is a local 32B; its
judge is *not* the same local 32B (the #382 failure at the model layer), it is sonnet — one
strength rank up, chosen automatically, **and never Fable**, because `costly: true` fences the
bump exactly as it fences the ladder. And had `sonnet` not existed, the judge would have stayed on
local-kimi with a #229 advisory rather than reaching for the costly model: **it degrades, it never
overspends.**

**The static-v1 story, made concrete by task 01:** a task mis-tagged `medium` that is really
`hard` does not silently climb to a stronger model — it **fails its deterministic gate and halts
honestly** for a human to re-tag it `hard` (or pin it). Correctness is never at risk; the gate,
not the model, certifies. (The v2 ladder would have auto-escalated att 2 to `hard` — a
convenience, not a correctness fix. #230-lite's numbers decide whether that convenience is worth
building.) There is no `--prefer` and no threshold prompt in v1: to lean harder on local you edit
a block's `routing.tiers` before the run. Run summary (#230-lite): `hard: 41k tok / $2.87 ·
medium: 190k tok / $0.14` (task 04's pinned spend is attributed to its pinned model).

## 15. Devil's-advocate self-critique

- **Strongest counter — "static-only ships the tagging burden without the recovery mechanism":**
  v1 asks the human to tag difficulty and to register `routing.tiers`, but withholds the ladder
  that would *recover* automatically from a mis-tag — so a mis-tagged `hard` task wastes its cheap
  attempts and then interrupts a human with a needs-human halt. Isn't the ladder the whole point?
  **Response:** No — the *token saving* is the point, and that comes entirely from the static
  resolver routing easy/medium work off the frontier; the ladder only changes what happens on the
  *mis-tagged minority*, and for those the gate already guarantees correctness (bad work fails,
  loudly). The cost of a mis-tag is bounded (a few cheap attempts + one re-tag), `maxCostUsd`
  bounds it deterministically, #229 pushes tag quality up *before* the run, and #230-lite makes
  the real mis-tag rate **measurable** — which is precisely the input needed to decide whether the
  ladder's added complexity (and its BLOCKER-grade OD-A edge cases) is worth building. Shipping the
  measurement before the mechanism is YAGNI done right.
- **"You deferred the ladder to dodge a hard design problem (OD-A/F1), not on merit":** partly
  fair — the F1 BLOCKER made the ladder *un-shippable as written*. **Response:** but the deferral
  is defensible on merit independently: the ladder is a convenience over a correctness-complete
  static floor, and #230-lite will tell us if it is even needed. The DA design is retained in §7
  so v2 starts from a ratified spec, not a blank page — deferral, not abandonment.
- **"Registry-in-promptRunners will bloat the block"** — three optional keys, one sentinel, zero
  new files, full reuse of overrides/validation; the alternative (a `providers.json`) costs a
  referential-integrity layer on day one for hypothetical vendors. YAGNI cuts toward the block.
- **"Deterministic routing forgoes the `notes` intelligence"** — yes, v1 routing reads only
  `tiers`/`strength`; the prose informs humans and composed prompts. An LLM router is precisely what
  invariant 1 forbids; the v2 prose-steering bet compiles intent into the structured surface.
- **"Invariant 7 is unprovable if activation is config-scoped":** correct, and that is exactly why
  this revision made activation **plan-scoped** (§4) — a routing-enabled config with a zero-tag
  plan does nothing tiering-specific, which the dedicated fixture in Invariant 7's acceptance
  pins down.
- **"Reserved-model-by-omitting-`routing` is a convention a reader will miss"** (the very trap the
  DA pass fell into) — **revision 3 concedes this and fixes it properly**: `costly: true` is a
  declared flag the generated registry (§4.3) puts in front of the user with its meaning in a
  comment, not a convention inferred from an absence. GR2051 remains as the back-door net.

**New counters revision 3 must answer:**

- **"Three axes plus `routing.tiers` is four things to annotate — you traded one blended tier for
  a form."** Fair, and it is the maintainer's own instruction (*"let's divide it out"*) taken at
  face value. **Response:** the cost is bounded by three properties, all deliberate. (1) **Every
  axis is optional**, and a registry with none behaves exactly as the reviewed design did. (2) The
  form is **generated and annotated by the harness** (§4.3), which is precisely why Decision 8
  exists — the burden the charter foresaw is the burden it already paid for. (3) Revision 3
  **removed** an axis (`rank`) while adding the mandated ones, so the net count of ordering
  concepts went *down*. If the maintainer still finds it heavy, the cut order is: `specialization`
  (only the judge tie-break reads it), then `tiering.verifier.minTier`.
- **"The costly floor is absolute — no dial, no `--force`. Isn't an un-overridable rule exactly the
  rigidity Guardrails avoids elsewhere?"** **Response:** it is un-overridable *by the harness*, not
  by the human — a task pin reaches any model instantly. That is the shape of every good safety
  floor here (`maxCostUsd` gates launches; the never-weaker floor has no auto-override either). The
  alternative — an autonomy dial that lets an unattended run spend the expensive model — is
  precisely what the maintainer forbade, and it is worth noting the *other* firm ruling in this
  repo's autonomy arc has the same shape (`dial:critical` + `proceed-unreviewed` is forbidden
  outright, not policy-gated). Autonomy floors in this product are floors.
- **"Adding the verifier half to v1 re-inflates the scope revision 2 deliberately cut."**
  **Response:** it adds one resolution rule and two advisory surfaces to a resolver that already
  exists in v1 — no new subsystem, no new runtime machinery, no probes, no ladder. The parts of the
  charter that WOULD be a subsystem (re-resolution on graduation, the floor) are
  deferred to v2 *with the ladder they depend on*, using the same test revision 2 used. And the
  registry axes had to land in Stage 1 regardless — the devil's-advocate gate said so, before the
  charter existed: *"the registry shape should absorb the comment-7 fix first."*
- **"You answered a settled charter Decision 9 with 'not in v1' — that is an override dressed as a
  phasing note."** **Conceded, and the maintainer overruled it (2026-08-12): Decision 9's "both" is
  v1.** The critique was right on the process point and, on inspection, right on the substance too:
  the deferral rested on reading the JIT check as *only* a graduation-observer, when its more
  durable job is to be **the real path checking itself** rather than a static model of it — the
  exact distinction #382 was about. §6.5 now states what the second boundary buys in a static v1
  without hand-waving, and the cost turned out to be one comparison over data §9.3 already writes.
  **The general lesson is worth keeping: "this would be a no-op in v1" is a claim about today's
  reachability, not about whether a structure belongs in the design.**

## 16. Decisions

**RESOLVED and in v1:**

D18 **v1 = static routing; the ladder/probes/steering are deferred to named v2 bets** — the
organizing decision (§2.2, §10) · D1 Stage-not-Wave terminology (§2.1) · D2 registry =
`promptRunners` generalized, no `providers.json` (§4) · D3 `effort` is NEW, opaque,
runner-translated (§4, corrects the "shipped with #200" misstatement) · D4 discriminator named
`kind`, default `"claude"`, GR2044 honest rejection (§4) · D6 routing guidance = structured
`tiers` + `strength` ordering + advisory `notes` prose, hard split (§4) · D7 tier enum `easy|medium|hard`,
closed, ordered, final for v1; difficulty ≠ strength; 4th tier additive-later (§5) · D9
precedence: full pin (`action.runner`/`action.model`) > tier resolution (`action.effort` alone
overrides effort, not a bypass) > legacy fallback (§6.1) · D13 absent `defaultTier` ⇒ untagged
tasks keep legacy resolution; **activation is plan-scoped** — configured iff any `routing` block,
active for a task only when it resolves through routing (§4) · D17 tier/effort edits stay inside
`TaskDefinitionHash` (drift applies; KISS) (§9.4) · **D19 tagging is gated on tiering being
configured** — `/plan-breakdown` writes nothing tiering-specific for a single-model user, so its
breakdown is byte-identical to today (§5, Invariant 7) · **D20 reserved-model pattern** — a block
with no `routing` is never a tier target; a reserved block must not be `default` (GR2051);
`/plan-breakdown`-time model choice is outside the registry (§4).

**RESOLVED and in v1 — added by revision 3 (the charter reconciliation):**

**D21 three independent model axes** — `costly` / `strength` / `specialization`, each admitting
*unspecified*, **top-level on the block, not inside `routing`** (a reserved or pinned block has a
strength too) (§4.1, charter D7) · **D21a the provider-kind fallback is verifier-only —
maintainer-ratified 2026-08-12** — `kind`
does not separate local from cloud (`openai-compat` covers both), so the fallback reads
`kind != "claude"` ⇒ weak-unless-declared and is never used for actor ordering: the guess is
allowed where being wrong costs one spare advisory, and forbidden where it would misroute real
spend (§4.1) · **D22 the costly floor** — the
harness never auto-selects a `costly: true` block, at any rung, by any mechanism, in any version;
only a task pin or the `default` pointer reaches one (§6.2, charter D3) · **D22a one candidacy
predicate** shared by the resolver, GR2048 and `no-route`, so validation and runtime can never
disagree about which blocks serve a rung (§6.2) · **D23 the registry is generated then annotated**
— `guardrails providers init` writes comment-annotated blocks into `guardrails.json` itself
(comments already parse), idempotently, never fabricating a model list (§4.3, charter D8) ·
**D24 the verifier route is v1 and static** — a judge resolves at the actor's rung, bumped when the
actor is weak, **advisory-only at BOTH boundaries, both of them v1: the startup preflight AND the
per-attempt JIT re-check** (charter D9, ratified 2026-08-12 over an earlier draft's proposal to
defer the JIT half; §6.5 states what the second boundary buys before graduation exists, and §6.5
carries the de-duplication rule that keeps three surfaces from shouting one finding)
(§6.5, charter D1/D2/D4/D5/D7/D9/D10) · **D24a the
bump is in STRENGTH, never in tier** — bumping the tier would mean "pretend the work is harder", a
category error; the charter's prose uses both phrasings and only one is coherent (§6.5) ·
**D25 `routing.rank` is retired** in favour of ascending-`strength` ordering — *the weakest model
that can serve the tier goes first*, a cost-minimising default the deterministic gate makes safe;
one ordering axis, not two with opposite polarity; retired-key warning GR2046 (§4.2; **settled
2026-08-12**) · **D26 the degrade/halt
asymmetry** — the verifier rule degrades to an advisory when only a costly model would satisfy it;
the actor route halts instead (GR2048 / `no-route`). Degrade what is advisory, halt what is
load-bearing; neither overspends (invariant 5, §6.2, §6.5) · **D27 the plan-wide verifier knob is a
FLOOR, not a default** — `tiering.verifier.minTier` never selects the judge's rung, it only refuses
one that came out below it and never lowers a result; it collapses charter Decisions 4 and 6 into
**one** floor concept, is **v1** (the judge's tier varies across tasks even in a static run, so a
floor is reachable — correcting revision 3's own YAGNI argument), is bypassed by a per-judge
`runner` pin but never by the advisory, and **degrades to an advisory rather than reaching a costly
block or climbing a rung** when it cannot be met (§6.5.1, settled 2026-08-12).

**DEFERRED to v2 (retained as ratified designs; each revisited with #230-lite data when/if its
bet is built):**

D5 escalated attempts draw from the SAME retry pool (§7, with #228) · D8 probes advise, never
gate; honest failure via shipped pause machinery (§6.4, with #227) — *note: the `no-route`
defensive outcome is v1 (§6.2)* · D10 threshold prompts ride `autonomyPolicy` with a new `routing`
boundary; no new knob (§8.2, with #231) · D11 probes are deterministic, never prompt spend,
TTL-cached (60 s) with consecutive-failure doubling, observe-only (§6.4, with #227) · D12 ambient
steering = structured `--prefer`; prose steering is a further v2 bet (§8.1, with #231) · D14 an
explicitly-pinned task never enters the ladder (§6.1 / §7, with #228) · D15 ladder triggers:
logic failures escalate immediately; timeout/max-turns/output-cap get one same-tier retry first
(§7, with #228) · D16 the ladder owns tier movement, the overwatcher layers guidance/budget on
top (§9.2, with #228).

## 17. Implementation handoff (after the #106 review of this draft)

**v1 — the two stages that ship:**

1. **Stage 1 (foundation) — `guardrails-harness-developer`:** `kind`/`effort`/`routing` **+ the
   three model axes `costly`/`strength`/`specialization` (§4.1)** on
   `RawPromptRunner`(+overrides)/`PromptRunnerConfig`, `tier`/`effort` on `RawAction`,
   `tiering` **(`defaultTier` + `verifier.minTier` only — NOT `thresholdPercent` /
   `probeCacheSeconds`, §12.7)** on
   `RawRunConfig`; `FromConfig` kind-switch; **GR2043–GR2053** in
   `PlanValidator`/`DiagnosticCodes` (marker bump to **GR2055**; GR2054 is v2, do NOT add) —
   **and RE-VERIFY the whole block against `DiagnosticCodes.cs` before landing, per §13's standing
   instruction**; §12.1/12.3 SSOT edits + the plan-breakdown `schemas.md` sentinel mirror.
   `filesTouched:
   src/Guardrails.Core/{Loading,Prompts}/**, docs/plans/02-…, .claude/skills/plan-breakdown/references/schemas.md`.
   ∥ **`guardrails-harness-developer` (second, independent slice):** **`guardrails providers init`**
   (§4.3) — per-`kind` enumeration behind an interface, comment-annotated emission into
   `guardrails.json`, idempotent merge, honest degradation when a kind cannot enumerate, diff
   presented for acceptance. `filesTouched: src/Guardrails.Cli/Commands/**,
   src/Guardrails.Core/Prompts/**`. *Depends on the axes schema, not on the resolver — it can land
   in parallel with the validator work.*
   ∥ **`guardrails-skill-author`:** plan-breakdown **gated** tagging doctrine + quality bar +
   report surface — and the ruling that a no-`routing` config produces a byte-identical breakdown
   (§5, D19). `filesTouched: .claude/skills/plan-breakdown/**`.
2. **Stage 2 (static consumers) — `guardrails-harness-developer`:** a **static** `TierResolver`
   (§6.1 precedence incl. `action.runner`/`action.effort`; §6.2 **the single candidacy predicate +
   the costly floor** + ascending-`strength` ordering + climb;
   §6.3 unavailability→shipped #115 pause; **§6.5 the verifier route — judge resolution, the
   strength bump, the advisory degradation, `guardrailOverrides` composing with the JUDGE's block,
   BOTH surfacing boundaries — the startup preflight AND the per-attempt JIT re-check with its
   de-duplication rule**; `no-route` outcome) + `TaskExecutor` wiring (replacing
   the ~1027–1032 two-level fallback), **provenance fields — landing whatever #349 has not, since
   #349 is still open** (§9.3) **plus the `judge` object** (§12.4),
   the **#230-lite per-tier spend line** with the Invariant-7 no-per-tier-line-when-inactive rule,
   §12.4/12.5 SSOT edits. **No probe, no ladder, no steering.** `filesTouched:
   src/Guardrails.Core/**, src/Guardrails.Cli/**, docs/plans/02-…`. ∥ **`guardrails-skill-author`:**
   guardrails-review #229 appropriateness check (graceful pre-tier skip) **+ the judge-weaker-than-
   actor and equal-and-weak advisory findings (§6.5)**. ∥
   **`guardrails-test-author`:** the resolution matrix (precedence × activation × GR codes), **the
   costly-floor matrix (own rung / climb / judge bump / `default` pointer / pin — the floor holds in
   the first three and yields in the last two)**, **the verifier matrix (weak actor bumps; strong
   actor does not; bump blocked by `costly` degrades to an advisory and the run proceeds;
   unspecified strength falls back to kind)**, the
   **Invariant-7 fixtures** (golden plans byte-identical + a routing-enabled/zero-tag plan doing
   zero tier-resolution **and zero judge-tiering activity**), and #230-lite aggregation goldens.

**v2 — named bets, NOT started until #230-lite measurement justifies them (§10, §11):**

3. **#227 probes** — `IProviderProbe` + cache (TTL doubling) + `providers status` (GR2054); gated
   by OD-C. **#228 ladder** — journal-derived rung, D15 trigger table, OD-A re-scoped per DA F1,
   `tierSource:"escalated"`, `routing.escalationTarget`; gated by OD-A/F5. **#231 steering** —
   `--prefer` + threshold boundary + `routing` `decisions[]` + OD-B carve-out + the DA-F2
   route-down option; gated by OD-B/F2. Each lands its §12.7 delta with its code.

**Standalone:**

4. **#223 — `guardrails-harness-developer`,** independently once a local endpoint exists: the
   `openai-compat` runner class behind GR2044's gate (§4.4).

Every stage: `guardrails-domain-knowledge` execution-semantics update in the same change.
