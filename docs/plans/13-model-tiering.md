# 13 — Model tiering (provider registry + static tier routing; ladder / probes / steering deferred to v2) — design of record (epic #201)

> **Status: DRAFT (revised) — for #106 inline draft-PR review.** This document is the
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
> candidate blocks whose `routing.tiers` includes it → order by `rank` → best block → emit
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
| Registry (`kind`, `routing`, `effort` on runner blocks) + tier fields + validation | **v1** | harness (`Guardrails.Core` loading/validation) + schema (§2/§3/§4.2 deltas, §12) |
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

## 4. The registry — `promptRunners` generalized (#224)  [v1]

**Ruling (D2):** there is no new `providers` section. A **runner block IS the routing unit**:
one `promptRunners.<name>` block = one concrete (provider `kind`, `command`/endpoint, `model`,
`effort`, settings) route. A provider exposing three models = three blocks sharing a `kind`.
This reuses the entire existing machinery — naming, `default` pointer, GR2004/GR2008/GR2009
validation, `guardrailOverrides`, `maxOutputTokens`, `env` — and keeps ONE schema under ONE
drift-tested sentinel. Rationale: a parallel `providers.json` would duplicate the
name-resolution and override surface and force a cross-file referential-integrity layer for
zero expressive gain (KISS; the #224 issue itself listed both options).

Three new optional keys per block (full JSON in §12.1):

- **`kind`** (D4) — the provider discriminator. **Default `"claude"`** (every existing config
  keeps working unchanged — the Stage-1 back-compat acceptance). v1 implements only
  `"claude"`; **`"openai-compat"` is the reserved seam #223 fills** (one kind covering
  Ollama / llama.cpp / LM Studio / vLLM — they share the wire protocol); `"codex"` /
  `"openrouter"` are reserved names, unassigned. A `kind` that is unrecognized OR recognized
  but not yet implemented in the installed harness fails `guardrails validate` with
  **GR2037**, naming the value and the currently supported set — an honest halt at validate
  time, never a silent fallback to Claude. `PromptRunnerRegistry.FromConfig` switches on
  `kind` to construct the runner class (the seam its own doc comment already names).
- **`effort`** (D3) — an opaque, per-block thinking-effort knob (e.g. `"low"`, `"xhigh"`).
  Opaque to the harness: shape-validated only (GR2042, mirroring GR2030's `model` check) and
  **translated by the runner CLASS** into whatever its CLI/API exposes — the spelling is
  quarantined exactly like `maxOutputTokens` → `CLAUDE_CODE_MAX_OUTPUT_TOKENS`. Wanting the
  same model at two efforts = two blocks (`"opus"`, `"opus-xhigh"`).
- **`routing`** (D6) — opts the block into tier resolution. **Absent ⇒ the block is NEVER a tier
  target** — reachable only explicitly (`action.runner`/`action.model`) or as the `default`
  pointer, exactly today's behavior. Shape: `{ "tiers": [...], "rank": N, "notes": "…" }` where
  `tiers` (required, non-empty subset of the tier enum) is the **machine-consumed** part — which
  tiers this route may serve; `rank` (optional, default 1, lower wins) orders same-tier
  candidates, ties broken by declaration order (deterministic); `notes` (optional prose) is
  surfaced to humans (`providers status`, review context) and MAY be appended to a composed
  prompt as context — **never parsed** (invariant 1). Malformed routing (empty/unknown `tiers`,
  non-positive `rank`, wrong types) is **GR2038**. The prose-vs-tags question is thereby
  answered: **both, with a hard deterministic/advisory split** (D6).

**Reserved-model pattern (D20 — answers review comment 7, "reserve Fable for /plan-breakdown;
re-attempts must never reach it").** Because a block with no `routing` is never a tier target,
reserving a bleeding-edge frontier model is simply *"give its block no `routing` block."* Such a
block can still be pinned explicitly (`action.runner`) but the resolver will never select it, so
no *tagged* task — and (once v2 exists) no *escalation* — can ever land on it. Two rules make the
reservation airtight:

- **A reserved block must NOT be the registry `default` pointer** — otherwise an untagged task
  with no `defaultTier` falls to legacy resolution = the default runner = the reserved model,
  and the reservation evaporates through the back door. `validate` **warns** when a `routing`-less
  block is named `default` in a config that declares tiering (the same net #229 provides at
  review time). *(This is why the worked example (§14) never makes a reserved block the default.)*
- **`/plan-breakdown`-time model choice is OUTSIDE this registry entirely.** The breakdown runs
  in the user's own Claude session, not through `promptRunners`; reserving a model *for authoring*
  is a session/tooling choice, not a routing config. The DoR states this so a reader does not
  expect a `promptRunners` knob for it.

**Deferred to v2 (with the ladder):** the DA pass proposed a `routing.escalationTarget: false`
field to express *"may serve a tier on first attempt but never RECEIVE a ladder escalation."*
That field is only meaningful once the ladder exists, so it is deferred to v2 with #228 (§7). In
static v1 the omit-`routing` reservation above fully covers the maintainer's requirement (a
reserved model is unreachable by any resolver, first attempt or retry).

**Tiering activation — configured vs. active (D13, absorbing DA F6 + the KISS gate):**

- **Configured** iff ≥ 1 block declares `routing`. This is what gates *tag authoring* (§5) and
  validation (a plan carrying tier tags with NO `routing` block anywhere is tiering-inert →
  **warning GR2041**, and the plan runs exactly as today).
- **Active for a task** only when that task will *actually resolve through routing* — i.e. it has
  an **effective tier** (a tag or `defaultTier`) **AND** a serving routing block exists. Tier
  resolution, and (in v2) any probing or threshold prompt, fire **only** for such work — **never
  merely because the config declares a `routing` block**. A run whose remaining tasks are all
  untagged resolves entirely via the legacy path and does nothing tiering-specific. This is what
  makes Invariant 7 *provable*: activation is plan-scoped, not config-scoped.

This makes the whole epic opt-in and every existing plan byte-compatible.

### 4.4 The #223 seam (defined here, not designed here)

#223 delivers an `IPromptRunner` class for `kind: "openai-compat"`: constructor
`(name, endpoint/command, model, effort, settings)` from its block; MUST preserve the verdict-file
contract (§4.2/§5), the `PromptFailureKind` classification quarantine (its own vendor error
strings live in its class, like `ClaudePromptRunner`'s), populate the same §7 provenance
fields, and report cost as absent (tokens only) unless its API provides one. When it lands,
GR2037's supported set grows — no other contract moves. Its internals (auth, streaming,
endpoint probing) are #223's own design space.

## 5. The tier model (#225)  [v1]

**Ruling (D7): the tier enum is `easy | medium | hard` — final for v1.** Closed, lowercase,
ordered `easy(1) < medium(2) < hard(3)` (the resolver's ordering; also the v2 ladder's rungs).
An unrecognized value anywhere (GR2039) is a validation error. Three levels is deliberately
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
  when they register a block — `routing.tiers` (which difficulties it serves), `rank` (preference
  among peers), `effort` (its thinking knob), `notes` (prose rationale). That *is* the human
  input on capability the comment asks about.
- **A 4th tier is additive-later, at zero cost.** "Final for v1" is not a one-way door: adding a
  tier is a purely additive schema change (a new enum value + blocks opting into it). We hold at
  three because the *deeper* fix for "more thinking levels" is the reserved-model / role
  restriction pattern (§4), not more difficulty rungs.

Where tags live:

- **`action.tier`** (task.json, prompt actions only) — mirrors the `action.model` pattern.
- **`tier`** frontmatter key on a `*.prompt.md` **judge guardrail** (§4.2 frontmatter, joining
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
  **GR2041 cannot fire**. A single-model user's breakdown is therefore **byte-identical to
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
   Shipped semantics unchanged.
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

### 6.2 Candidate selection — never weaker than asked  [v1]

Candidates for rung R = routing-enabled blocks with R ∈ `routing.tiers`, ordered by `rank`, then
declaration order (both deterministic). The best candidate wins. If R has **no** candidates,
climb to the nearest **stronger** served rung (loud log line + provenance records the climb). *(In
v2, steering §8 prepends a bias before `rank`, and probe state §6.4 sinks exhausted blocks — but
the never-weaker-than-asked floor holds in every version.)*

**Routing DOWN a rung is never automatic.** In v1 the only lever below the never-weaker floor is
**halt-and-edit-config** (change a block's `routing.tiers`, re-run). *(The v2 steering design
adds a human-sanctioned mid-run "serve tier X from block Y for the rest of this run" option; that
is the DA-F2 route-down contradiction, resolved by deferral — there is no half-built downward
lever in v1.)*

Statically, `validate` errors (**GR2040**) when any *used* tier (a task tag, frontmatter tag, or
`defaultTier`) has no served rung at-or-above it — the only config where resolution would have to
route down.

**The `no-route` defensive outcome [v1].** The **`no-route`** attempt outcome (§12.4) exists only
for the defensive residual — resolution finds literally zero registered candidate blocks at
runtime for a used rung (a config gap GR2040 should have caught) — and settles needs-human with
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
**60**, GR2043 if ≤ 0), with **consecutive-failure TTL doubling** (cap ~15 min) so a dead endpoint
is not re-probed every minute all run (DA comment 6); probed lazily at resolution and at run
start, with a small hard per-probe timeout; observe-only (not journaled — they surface in
`decisions[]` context, threshold prompts, and a new **`guardrails providers status`** command). A
free retrospective signal is available at zero probe cost: #115's runner quarantine already parses
reset hints / limit phrases out of live responses, so a prior attempt's parsed limit signal is
per-window data the probe layer can ingest. **Feasibility of a *stable* Claude usage probe is the
reason probes are deferred, not merely phased** — see OD-C (§11), now a v2 decision.

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

### 9.3 Journal / provenance (#198, #230-lite) — additive over #349's base  [v1]

**Sequencing with #349.** #349 (pilot-seat model provenance — the `resolvedModel` / `effort`
journal fields) is being dogfooded first and **lands the provenance base**. This DoR's journal
delta is therefore **trimmed to what is additive over that base:** per-attempt `provenance` gains
only **`runner`** (resolved block name), **`kind`**, **`tier`** (the rung that resolved), and
**`tierSource`** (`task | plan-default | override` in v1; `escalated` is added by the v2 ladder);
plus an optional per-attempt **`usage { inputTokens, outputTokens }`** so a costless local
provider still shows volume for #230-lite (if #349 has not already added it). Absent-not-null
throughout; old journals read fine.

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
| **Stage 1** (`model-tiering-foundation.md`) | #224 registry (`kind`/`effort`/`routing` + GR2037/38/39/40/41/42 validation + sentinel update; reserved-model warning §4) ∥ #225 **gated** tagging (`action.tier`, frontmatter `tier`, `tiering.defaultTier`, skill doctrine — writes nothing when tiering unconfigured, §5) | this DoR reviewed |
| **Stage 2** (`model-tiering-consumers.md`, static subset) | #226-**static** resolver (§6.1 precedence incl. `action.runner`/`action.effort`, §6.2 candidate selection, §6.3 unavailability→#115, `no-route`, provenance fields §9.3) ∥ #229 review check ∥ #230-**lite** per-tier spend line | Stage 1 |
| **#223** (standalone) | `openai-compat` runner class filling the §4.4 seam | Stage 1 (the `kind` seam) + real local endpoint available |

**v2 — named bets (deferred; revisited with #230-lite measurement in hand):**

| Bet | Contents | Gating decision |
|---|---|---|
| **#227 probes** | per-`kind` `IProviderProbe` + cache (TTL doubling) + `guardrails providers status` (GR2043) + probe-advise ranking (§6.4) | OD-C (stable Claude usage surface feasible?) |
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
holds exactly ONE key — `defaultTier`;** the `thresholdPercent` / `probeCacheSeconds` knobs are
v2 (they configure probes and threshold prompts — see §12.7).

```jsonc
  "tiering": {                        // OPTIONAL (#201). Tiering is CONFIGURED iff >=1 runner block declares
                                      //   `routing` (below); ACTIVE for a task only when it resolves through
                                      //   routing (§4). This block only holds the plan-wide default. Absent = none.
    "defaultTier": "medium"           // OPTIONAL plan-wide tier for UNTAGGED prompt actions: "easy"|"medium"|"hard"
                                      //   (GR2039 if unrecognized). EXAMPLE value — there is NO built-in default;
                                      //   absent = an untagged task keeps LEGACY resolution (§5).
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
                                      //   Unrecognized OR not-yet-implemented kind = GR2037 (never a silent
                                      //   fallback to claude).
      "effort": null,                 // OPTIONAL thinking-effort knob (#201); OPAQUE string, shape-checked like
                                      //   `model` (GR2042), TRANSLATED by the runner CLASS (spelling quarantined
                                      //   there, like maxOutputTokens). Same model at two efforts = two blocks.
      "routing": {                    // OPTIONAL (#224): opts this block into tier resolution (§9.6). Absent =
                                      //   block reachable only explicitly / as default (today's behavior).
        "tiers": ["medium", "hard"],  // REQUIRED here; non-empty subset of easy|medium|hard — which rungs this
                                      //   (kind, model, effort) route may serve. Malformed = GR2038.
        "rank": 1,                    // OPTIONAL preference among same-rung candidates; lower wins; default 1;
                                      //   ties broken by declaration order (deterministic). Non-positive = GR2038.
        "notes": "…"                  // OPTIONAL human-authored prose guidance; surfaced to humans and MAY be
                                      //   appended to composed prompts as context — NEVER parsed for routing.
      },
```

Prose bullets to add under §2: the **configured-vs-active** activation rule (configured iff ≥1
`routing` block; *active for a task* only when it has an effective tier AND a serving block —
plan-scoped, §4); tags without any routing block = **GR2041 warning**, plan runs as today; the
GR2040 rule (§12.5); and the reserved-model warning (a `routing`-less block must not be `default`,
§4).

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
                                     //   attempt-launch tier resolution (§9.6); GR2039 if unrecognized. null/absent
                                     //   = inherit tiering.defaultTier (§2), else legacy resolution.
    "effort": null,                  // prompt actions only (#201): per-task thinking-effort override; mirrors
                                     //   `model`'s SHAPE (GR2042 shape check; opaque to the harness) but NOT its
                                     //   bypass — with a tier but no full pin, resolution still selects the block
                                     //   and `effort` overrides the resolved route's effort (§6.1 item 2).
```

Replace the `action.model` resolution-order sentence with: "**Full pin — `action.runner` or
`action.model`** (if set — bypasses tier resolution entirely) **> tier resolution (§9.6, when an
effective tier exists and a block serves it; `action.effort` alone overrides the resolved route's
effort without bypassing) > `promptRunners.<name>.model` > the CLI's own default.**" Add: a
`validate` **warning** when a full pin and `action.tier` coexist on the same action (the tier is
dead weight). Also §4.2: frontmatter gains the optional `tier` key (judge guardrails; resolution
applies).

### 12.4 §7 journal — Stage 2 [v1] (additive over #349's provenance base)

- `provenance` gains additive optional fields **on top of #349's `resolvedModel`/`effort` base**:
  `"runner"` (resolved block name), `"kind"`, `"tier"` (the rung that resolved), `"tierSource"`:
  `"task" | "plan-default" | "override"` (the `"escalated"` value is added by the v2 ladder —
  §12.7). Absent (never null noise) for script attempts / legacy journals.
- Attempt record gains optional `"usage": { "inputTokens": 0, "outputTokens": 0 }` (additive; the
  tokens-only accounting surface for costless providers, #230-lite — unless #349 already carries
  it).
- Attempt `outcome` enum gains **`no-route`** — resolution found zero registered candidate blocks
  at-or-above the task's rung (a runtime config gap; validation GR2040 normally prevents it).
  Settles needs-human with "register a provider serving tier ≥ R" feedback. This is a v1 defensive
  outcome independent of probes (§6.2).
- *(v2, §12.7: `decisions[]` `boundary` gains `routing`.)*

### 12.5 §9 — seam note + a new §9.6 "Tier routing (model tiering, #201)" [v1]

§9 intro: note that `FromConfig` switches on `kind` (GR2037 gate) and that `--model`/effort flags
are emitted from the RESOLVED route. New **§9.6 (v1 content)** documenting, normatively: the
precedence chain incl. `action.runner`/`action.effort` (§6.1); candidate selection +
never-route-down + nearest-stronger-rung climb (§6.2); the `no-route` defensive outcome;
provider-unavailability → the shipped #115 pause + never-weaker-hold (§6.3); the plan-scoped
configured-vs-active activation rule (§4). (Content = this DoR's §4–§6, compressed to contract
language.) **§9.6 explicitly states there is no ladder, no probe, and no steering in v1.**

### 12.6 Validation summary [v1] (GR text in §13)

- GR2009's runner-command probe extends per-kind (an `openai-compat` block probes its endpoint
  reachability as a **warning**, mirroring the PATH probe — lands with the #223 standalone runner).
- Two new **warnings** (v1, DA-derived): a `routing`-less block named `default` in a
  tiering-configured file (reserved-model back-door, §4); a **full pin + `action.tier`** coexisting
  on one action (§6.1). Both are warnings, not errors — the plan still runs.

### 12.7 v2-deferred deltas — do NOT ship in v1

Consolidated so a Stage-1/2 implementer can see exactly what to leave out. Each lands with its
v2 bet (§10), in the same change as its code:

- **`tiering.thresholdPercent` (default 80) + `tiering.probeCacheSeconds` (default 60, GR2043 if
  ≤0)** keys — with #231 / #227 respectively.
- **§2.1 `autonomyPolicy` `routing` boundary** + the non-interactive carve-out (§12.2) — with #231.
- **`decisions[]` `boundary: "routing"`** — with #231.
- **`provenance.tierSource` value `"escalated"`** — with #228.
- **`routing.escalationTarget: false`** block field — with #228 (§4, §7).
- **GR2043** (`RoutingNumericNonPositive`) — with #227 (the only reserved code that is a v2 delta;
  GR2037–GR2042 are all v1 — §13).
- **§9.6 normative language** for probes-advise / the ladder / `--prefer` + threshold prompts /
  the ladder-first-overwatcher-layers ordering — with the respective bet.

## 13. Reserved diagnostic codes — GR2037–GR2045 (next-free marker → GR2046)

Verified against `DiagnosticCodes.cs` at authoring time: **GR2036** (`ExpectedDurationNonPositive`,
issue #331) is the last taken code and the file's marker says GR2037 is next-free. (The epic
briefing said GR2036 — stale; the file wins.) This DoR **reserves the contiguous block
GR2037–GR2045**; the constants + the historical comment discipline below land in
`DiagnosticCodes.cs` at build time, per stage, and the marker line is bumped to GR2046. The
**Scope** column marks which land in v1 vs a v2 bet. GR2043 is the ONLY code deferred to v2.

| Code | Name | Sev | Scope | Meaning |
|---|---|---|---|---|
| GR2037 | `UnsupportedRunnerKind` | error | **v1** | `promptRunners.<name>.kind` unrecognized, or recognized but not implemented in this harness build; message names the value + the supported set (the #223 seam gate — never a silent claude fallback) |
| GR2038 | `MalformedRoutingGuidance` | error | **v1** | `routing` block invalid: missing/empty `tiers`, a value outside the tier enum, non-positive `rank`, or wrong types |
| GR2039 | `UnrecognizedTier` | error | **v1** | `action.tier`, judge-frontmatter `tier`, or `tiering.defaultTier` not one of `easy\|medium\|hard` |
| GR2040 | `UnservableTier` | error | **v1** | a USED tier (task tag, frontmatter tag, or `defaultTier`) in a tiering-configured plan has no routing-enabled block at-or-above it (resolution would have to route weaker than asked) |
| GR2041 | `TieringInert` | warning | **v1** | tier tags present but NO block declares `routing` — tags have no effect; plan runs with legacy resolution |
| GR2042 | `EffortInvalid` | error | **v1** | a present `effort` (block, override, or `action.effort`) fails the GR2030-style shape check (non-empty, no whitespace/control chars) |
| GR2044 | `ReservedBlockIsDefault` | warning | **v1** | a `routing`-less block is the registry `default` pointer in a tiering-configured file — untagged tasks would fall to a model reserved out of routing (§4, DA comment 7) |
| GR2045 | `PinAndTierCoexist` | warning | **v1** | a full pin (`action.runner`/`action.model`) and `action.tier` are both set on one action — the tier is dead weight the pin overrides (§6.1, DA F3) |
| GR2043 | `RoutingNumericNonPositive` | error | **v2 (#227)** | `tiering.probeCacheSeconds` / `thresholdPercent` present but not a positive value (cf. GR2012/GR2023/GR2036) |

Historical-comment discipline for the build-time edit: "Next-free allocation confirmed at
authoring time of the model-tiering DoR (`docs/plans/13-model-tiering.md`): GR2036
(ExpectedDurationNonPositive) is the last taken; GR2037–GR2045 are the reserved CONTIGUOUS
model-tiering block (#201). **v1 (static routing)** takes GR2037–GR2042 (#224/#225) + GR2044/GR2045
(DA-derived warnings) in Stages 1–2; **v2** takes GR2043 with #227's probes. CURRENT next-free
code: GR2046."

## 14. Worked example  [v1 — static routing]

`guardrails.json` (v1 target state; until #223 lands, the `local-kimi` block fails validation with
GR2037 naming `openai-compat` — delete it to run on a claude-only box today). Note the **`fable`
reserved block**: it declares **no `routing`**, so the resolver never selects it — a re-attempt
can never reach Fable (review comment 7). It is deliberately **NOT** the `default` pointer (that
would trip GR2044 and route untagged tasks to it); `default` is `sonnet`, a routing block.

```jsonc
{
  "version": 1,
  "maxCostUsd": 10.00,
  "tiering": { "defaultTier": "medium" },            // v1: defaultTier is the ONLY tiering key
  "promptRunners": {
    "default": "sonnet",                              // a ROUTING block, never the reserved fable block
    "fable":      { "command": "claude", "model": "claude-fable-5", "effort": "xhigh" },
                    // RESERVED: no `routing` => never a tier target; usable only via an explicit
                    //   action.runner pin. /plan-breakdown-time model choice is outside this
                    //   registry entirely, so reserving Fable "for authoring" needs no knob here.
    "opus":       { "command": "claude", "model": "claude-opus-4-6", "effort": "high",
                    "routing": { "tiers": ["hard"], "rank": 1,
                                 "notes": "cross-module architecture, retry/journal contract work" } },
    "sonnet":     { "command": "claude", "model": "claude-sonnet-4-5",
                    "routing": { "tiers": ["medium", "hard"], "rank": 2,
                                 "notes": "typical single-module coding; hard fallback when opus busy" } },
    "local-kimi": { "kind": "openai-compat", "command": "http://inference.local:11434",
                    "model": "kimi-70b",
                    "routing": { "tiers": ["easy", "medium"], "rank": 1,
                                 "notes": "mechanical refactors, doc/skill updates, migrations; free" } }
  }
}
```

Tasks: `01-author-stats-tests` (`"tier": "medium"`), `02-implement-stats` (`"tier": "hard"`),
`03-update-docs` (untagged → defaultTier `medium`), `04-hand-added-hotfix` (human-added,
`action.model: "claude-opus-4-6"` pinned).

| Attempt | Effective rung | Candidates (ordered) | Resolves to | Provenance |
|---|---|---|---|---|
| 01 att 1 | medium | local-kimi(r1), sonnet(r2) | **local-kimi** / kimi-70b | tier=medium, tierSource=task |
| 01 att 2 (guardrail-failed) | medium (**no ladder in v1 — same tier**) | local-kimi(r1), sonnet(r2) | **local-kimi** again | tierSource=task |
| 01 att 3 (still failing) | medium | — | budget exhausted → **needs-human** (re-tag to `hard`, or pin) | honest halt |
| 02 att 1 | hard | opus(r1), sonnet(r2) | **opus** | tierSource=task |
| 03 att 1 | medium (default) | local-kimi, sonnet | **local-kimi** | tierSource=plan-default |
| 04 att 1 | — (pinned) | — (bypasses resolution) | claude-opus-4-6 | tierSource=override |

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
  `tiers`/`rank`; the prose informs humans and composed prompts. An LLM router is precisely what
  invariant 1 forbids; the v2 prose-steering bet compiles intent into the structured surface.
- **"Invariant 7 is unprovable if activation is config-scoped":** correct, and that is exactly why
  this revision made activation **plan-scoped** (§4) — a routing-enabled config with a zero-tag
  plan does nothing tiering-specific, which the dedicated fixture in Invariant 7's acceptance
  pins down.
- **"Reserved-model-by-omitting-`routing` is a convention a reader will miss"** (the very trap the
  DA pass fell into) — mitigated by making it explicit in §4/§14 *and* by GR2044, which turns the
  one dangerous mistake (reserved block = `default`) into a validate warning rather than a silent
  leak.

## 16. Decisions

**RESOLVED and in v1:**

D18 **v1 = static routing; the ladder/probes/steering are deferred to named v2 bets** — the
organizing decision (§2.2, §10) · D1 Stage-not-Wave terminology (§2.1) · D2 registry =
`promptRunners` generalized, no `providers.json` (§4) · D3 `effort` is NEW, opaque,
runner-translated (§4, corrects the "shipped with #200" misstatement) · D4 discriminator named
`kind`, default `"claude"`, GR2037 honest rejection (§4) · D6 routing guidance = structured
`tiers`/`rank` + advisory `notes` prose, hard split (§4) · D7 tier enum `easy|medium|hard`,
closed, ordered, final for v1; difficulty ≠ strength; 4th tier additive-later (§5) · D9
precedence: full pin (`action.runner`/`action.model`) > tier resolution (`action.effort` alone
overrides effort, not a bypass) > legacy fallback (§6.1) · D13 absent `defaultTier` ⇒ untagged
tasks keep legacy resolution; **activation is plan-scoped** — configured iff any `routing` block,
active for a task only when it resolves through routing (§4) · D17 tier/effort edits stay inside
`TaskDefinitionHash` (drift applies; KISS) (§9.4) · **D19 tagging is gated on tiering being
configured** — `/plan-breakdown` writes nothing tiering-specific for a single-model user, so its
breakdown is byte-identical to today (§5, Invariant 7) · **D20 reserved-model pattern** — a block
with no `routing` is never a tier target; a reserved block must not be `default` (GR2044);
`/plan-breakdown`-time model choice is outside the registry (§4).

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

1. **Stage 1 (foundation) — `guardrails-harness-developer`:** `kind`/`effort`/`routing` on
   `RawPromptRunner`(+overrides)/`PromptRunnerConfig`, `tier`/`effort` on `RawAction`,
   `tiering` **(`defaultTier` only — NOT `thresholdPercent`/`probeCacheSeconds`, §12.7)** on
   `RawRunConfig`; `FromConfig` kind-switch; **GR2037–GR2042 + GR2044 + GR2045** in
   `PlanValidator`/`DiagnosticCodes` (marker bump to **GR2046**; GR2043 is v2, do NOT add);
   §12.1/12.3 SSOT edits + the plan-breakdown `schemas.md` sentinel mirror. `filesTouched:
   src/Guardrails.Core/{Loading,Prompts}/**, docs/plans/02-…, .claude/skills/plan-breakdown/references/schemas.md`.
   ∥ **`guardrails-skill-author`:** plan-breakdown **gated** tagging doctrine + quality bar +
   report surface — and the ruling that a no-`routing` config produces a byte-identical breakdown
   (§5, D19). `filesTouched: .claude/skills/plan-breakdown/**`.
2. **Stage 2 (static consumers) — `guardrails-harness-developer`:** a **static** `TierResolver`
   (§6.1 precedence incl. `action.runner`/`action.effort`; §6.2 candidate selection + climb;
   §6.3 unavailability→shipped #115 pause; `no-route` outcome) + `TaskExecutor` wiring (replacing
   the ~1027–1032 two-level fallback), **provenance fields additive over #349's base** (§9.3),
   the **#230-lite per-tier spend line** with the Invariant-7 no-per-tier-line-when-inactive rule,
   §12.4/12.5 SSOT edits. **No probe, no ladder, no steering.** `filesTouched:
   src/Guardrails.Core/**, src/Guardrails.Cli/**, docs/plans/02-…`. ∥ **`guardrails-skill-author`:**
   guardrails-review #229 appropriateness check (graceful pre-tier skip). ∥
   **`guardrails-test-author`:** the resolution matrix (precedence × activation × GR codes), the
   **Invariant-7 fixtures** (golden plans byte-identical + a routing-enabled/zero-tag plan doing
   zero tier-resolution), and #230-lite aggregation goldens.

**v2 — named bets, NOT started until #230-lite measurement justifies them (§10, §11):**

3. **#227 probes** — `IProviderProbe` + cache (TTL doubling) + `providers status` (GR2043); gated
   by OD-C. **#228 ladder** — journal-derived rung, D15 trigger table, OD-A re-scoped per DA F1,
   `tierSource:"escalated"`, `routing.escalationTarget`; gated by OD-A/F5. **#231 steering** —
   `--prefer` + threshold boundary + `routing` `decisions[]` + OD-B carve-out + the DA-F2
   route-down option; gated by OD-B/F2. Each lands its §12.7 delta with its code.

**Standalone:**

4. **#223 — `guardrails-harness-developer`,** independently once a local endpoint exists: the
   `openai-compat` runner class behind GR2037's gate (§4.4).

Every stage: `guardrails-domain-knowledge` execution-semantics update in the same change.
