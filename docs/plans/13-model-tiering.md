# 13 — Model tiering (provider registry + tier routing + escalation) — design of record (epic #201)

> **Status: DRAFT — for #106 inline draft-PR review.** This document is the contract-locked,
> build-ready design of record for the model-tiering epic (#201) and its sub-issues
> #223–#231. It **ratifies** the three existing stage briefs — `model-tiering-foundation.md`
> (Stage 1: #224 + #225), `model-tiering-consumers.md` (Stage 2: #226 + #227 + #229 + #230),
> `model-tiering-dynamic-behavior.md` (Stage 3: #228 + #231) — as the plan-of-record for scoping
> and acceptance; where this DoR and a stage brief differ, **this DoR wins** (the deltas are
> called out in §2.2). Implementation does not begin until this draft PR's inline review
> comments are addressed (the #106 gate).

This document is the SSOT-companion for the contract deltas proposed in §12. Nothing in
`02-schemas-and-contracts.md` is mutated by this change — §12 is a verbatim-appliable proposal
that lands **at build time, stage by stage, in the same change as the code it describes**
(SSOT invariant 4). Where this doc and the live SSOT ever differ after landing, the SSOT wins
for the wire contract; this doc owns the rationale, the rulings, and the phasing.

---

## 1. What it is, and the pain it removes

Route each prompt attempt to a difficulty-appropriate (provider, model, effort) instead of
spending frontier tokens on everything. Observed live (preflights dogfood): 4 parallel tasks
burned a usage limit 4% → 29% in ~9 minutes, much of it on routine work (baselines, doc/skill
updates, mechanical migrations) that did not need a frontier model.

**The load-bearing thesis (from #201):** *deterministic guardrails make cheaper models safe.*
A task is certified by its deterministic gate — "a prompt may propose, only a deterministic
gate may certify" — so the model that produced the work matters less: a weaker/local model's
output either passes the gate or it does not. That is what makes tiering low-risk here versus
a bare LLM pipeline where model quality is the only backstop, and it is why the escalation
ladder (§7) can be a plain deterministic retry policy rather than a judgment call.

The design splits **what** is decided from **when** (the #201 "Resolution timing" ruling,
2026-07-04, reaffirmed here):

1. The **difficulty tag** (`easy | medium | hard`) is **static** — set by `/plan-breakdown`
   (#225) or a human hand-edit; untagged tasks inherit a plan-wide default.
2. The concrete **(provider, model, effort)** is **dynamic** — resolved deterministically by
   the harness at **attempt-launch time** (#226), fresh on every attempt including retries,
   against the current registry (#224) + probe state (#227) + steering (#231).
3. `action.model` (shipped, #200) remains the explicit escape hatch that bypasses resolution
   entirely.
4. The **escalation ladder** (#228) is the safety net when the tag is wrong or absent: a
   guardrail-failed attempt escalates the next attempt one rung stronger.
5. `guardrails-review` (#229) is the pre-run check for missing/mismatched tags.

## 2. Placement, and the three stage briefs

| Slice | Placement |
|---|---|
| Registry (`kind`, `routing`, `effort` on runner blocks) + tier fields + validation | harness (`Guardrails.Core` loading/validation) + schema (§2/§3/§4.2 deltas, §12) |
| Attempt-launch tier resolution + escalation ladder + `no-route` outcome | harness (`TaskExecutor` / a new `TierResolver` in `Guardrails.Core.Prompts`) |
| Budget/limit probes + `guardrails providers status` | harness (`Guardrails.Core` per-kind probe classes) + CLI |
| Difficulty tagging doctrine | skill (`plan-breakdown`) + schema (§3 delta) |
| Model-appropriateness check | skill (`guardrails-review`) — advisory findings only |
| Cost/token accounting by tier | harness (run-summary aggregation over §7 provenance) |
| Threshold prompts + ambient steering | harness/CLI, governed by the **shared** §2.1 `autonomyPolicy` (new `routing` boundary) — **no new policy field** |
| Concrete non-Claude runner (local OpenAI-compatible endpoint) | **#223, standalone** — plugs into the `kind` seam (§4.4); its internals are NOT designed here |
| Prose/free-text steering interpretation; per-model $ pricing tables; overwatcher tier-pinning | **v2 bets** (§10) |

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
rewritten. This DoR changes/settles the following on top of them:

- **Stage-1 registry shape settled** (D2, §4): the registry is `promptRunners` *generalized*
  (a `kind` + `routing` extension of the existing blocks), NOT a new sibling section or
  `providers.json` (#224 left this open).
- **`action.effort` corrected** (D3, §5): #200 shipped `action.model` only. Every reference to
  "`action.model`/`action.effort` (already shipped, #200)" in the briefs/issues overstates —
  `effort` is a **new** field this epic introduces (Stage 1 schema, Stage 2 consumption).
- **Stage-2 #226 item 4 revised** (D8, §6.3): probes **advise ranking, never gate launch** —
  the honest-failure path for an exhausted provider is the *shipped* transient-pause /
  `rate-limited` machinery (#115), not a new resolution failure. `no-route` exists only as a
  defensive outcome for a genuine runtime config gap.
- **Stage-3 #228 item 3 resolved** (D5, §7): an escalated attempt draws from the **same** retry
  pool — no reset.
- **Stage-3 #231 unattended behavior reconciled with §2.1** (D10, §8.2): the routing boundary's
  non-interactive `prompt` default is *proceed with unchanged routing* (status quo = no
  application, so no sanction needed), not an exit-2 halt — flagged for sign-off (**OD-B**).
- **Terminology** (D1, §2.1).

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
5. **Honest halts.** The ladder changes WHICH model retries use, never WHETHER a stuck task
   surfaces to a human. Resolution never silently routes *weaker* than asked (§6.2). The
   threshold prompt's unattended default is the do-nothing status quo, loudly logged.
6. **Plain files, light setup.** Probes are stateless HTTP/CLI queries with an in-memory TTL
   cache; no daemon, no database.

## 4. The registry — `promptRunners` generalized (Stage 1, #224)

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
- **`routing`** (D6) — opts the block into tier resolution. Absent ⇒ the block is reachable
  only explicitly (`action.runner`) or as the default — exactly today's behavior. Shape:
  `{ "tiers": [...], "rank": N, "notes": "…" }` where `tiers` (required, non-empty subset of
  the tier enum) is the **machine-consumed** part — which tiers this route may serve; `rank`
  (optional, default 1, lower wins) orders same-tier candidates, ties broken by declaration
  order (deterministic); `notes` (optional prose) is surfaced to humans (threshold prompts,
  `providers status`, review context) and MAY be appended to a composed prompt as context —
  **never parsed** (invariant 1). Malformed routing (empty/unknown `tiers`, non-positive
  `rank`, wrong types) is **GR2038**. The prose-vs-tags question is thereby answered: **both,
  with a hard deterministic/advisory split** (D6).

**Tiering activation rule:** tier resolution is active iff ≥ 1 block declares `routing`. Tags
on tasks with NO routing-enabled block anywhere = tiering inert → validate **warning GR2041**
(the plan still runs exactly as today). This makes the whole epic opt-in and every existing
plan byte-compatible.

### 4.4 The #223 seam (defined here, not designed here)

#223 delivers an `IPromptRunner` class for `kind: "openai-compat"`: constructor
`(name, endpoint/command, model, effort, settings)` from its block; MUST preserve the verdict-file
contract (§4.2/§5), the `PromptFailureKind` classification quarantine (its own vendor error
strings live in its class, like `ClaudePromptRunner`'s), populate the same §7 provenance
fields, and report cost as absent (tokens only) unless its API provides one. When it lands,
GR2037's supported set grows — no other contract moves. Its internals (auth, streaming,
endpoint probing) are #223's own design space.

## 5. The tier model (Stage 1, #225)

**Ruling (D7): the tier enum is `easy | medium | hard` — final for v1.** Closed, lowercase,
ordered `easy(1) < medium(2) < hard(3)` (the ladder's rungs). An unrecognized value anywhere
(GR2039) is a validation error. Three levels is deliberately coarse: the tag must stay a cheap,
stable judgment a human can make without knowing what is registered (#201's rationale); finer
gradations would re-couple tagging to model knowledge.

Where tags live:

- **`action.tier`** (task.json, prompt actions only) — mirrors the `action.model` pattern.
- **`tier`** frontmatter key on a `*.prompt.md` **judge guardrail** (§4.2 frontmatter, joining
  `runner`/`maxTurns`) — so #225's "and any surviving judge-guardrail" has a concrete surface.
- **`tiering.defaultTier`** (guardrails.json, optional) — the plan-wide default for untagged
  prompt actions (including one a human hand-adds later). **Absent ⇒ an untagged task follows
  the legacy resolution path** (runner default), even when tiering is active — the feature
  never captures a task nobody classified (D13).

`/plan-breakdown` classifies every prompt-action task (and surviving judge guardrail), writes
the tag, and **surfaces each classification + a one-line why in the breakdown report** (the
#42 surface-the-choice discipline — never silent). The skill's quality-bar checklist gains the
doctrine entry (mirroring #94's maxTurns-by-archetype precedent).

## 6. Attempt-launch resolution (Stage 2, #226)

Runs immediately before **every** attempt launch, including retries. Deterministic, in the
harness, replacing today's two-level `ResolveModelForDisplay(task.Action.Model, runnerModel)`
fallback (`TaskExecutor.cs` ~1027–1032) with:

### 6.1 Precedence (D9 — the full steering/config order)

1. **`action.model` / `action.effort`** (task.json) — explicit always wins; bypasses tier
   resolution AND the ladder entirely (a pinned task never escalates, D14). Shipped semantics
   unchanged.
2. **Tier resolution** (when the task has an effective tier and tiering is active):
   effective tier = `action.tier` (or judge frontmatter `tier`) ?? `tiering.defaultTier`;
   rung = effective tier adjusted by the ladder (§7); route = best candidate block (§6.2),
   biased by steering (§8) — where a **mid-run interactive decision supersedes the CLI
   `--prefer` flag, which supersedes `guardrails.json`** for the remainder of the run (most
   recent human intent wins; every supersession is a `decisions[]` entry).
3. **Legacy fallback** — no tier / tiering inactive: `promptRunners.<name>.model` else CLI
   default, exactly today.

### 6.2 Candidate selection — never weaker than asked

Candidates for rung R = routing-enabled blocks with R ∈ `routing.tiers`, ordered by steering
bias, then `rank`, then declaration order. If R has **no** candidates, climb to the nearest
**stronger** served rung (loud log line + provenance records the climb). Routing **down** a
rung is never automatic — only a human steers downward (§8). Statically, `validate` errors
(**GR2040**) when any *used* tier (a task tag, frontmatter tag, or `defaultTier`) has no
served rung at-or-above it — the only config where resolution would have to route down.

### 6.3 Probes advise, the pause machinery enforces (D8 — revises Stage-2 #226 item 4)

Probe state (§6.4) **re-orders and annotates** candidates (an exhausted provider's blocks sink
below serviceable ones; `unknown` counts as serviceable); it never vetoes a launch. If every
candidate at-or-above the rung is probe-exhausted, the harness launches the best candidate
anyway and lets the **shipped** transient-pause machinery (#115) ride the limit out — bounded
by `transientPauseBudgetSeconds`, settling `rate-limited`/needs-human honestly on exhaustion.
Rationale: probes are advance estimates and can be stale/wrong; the runner's live 429 is
ground truth, and its handling already exists — a parallel probe-gated failure path would be
a second, weaker copy of it. The **`no-route`** attempt outcome (§12.4) exists only for the
defensive residual — resolution finds literally zero registered candidate blocks at runtime
(a config gap validation should have caught) — and settles needs-human with an actionable
"register a provider serving tier ≥ R" message.

### 6.4 Probes (Stage 2, #227)

Per-`kind` probe classes (`IProviderProbe`), returning
`{ status: ok | nearing-limit | exhausted | unknown, headroom?, detail, probedAt }`:
Claude = the CLI/account's usage surface where one exists (weekly-plan %, 5-hour window);
openai-compat = endpoint reachability/load; a kind with no usage surface returns `unknown`
(never fails the run). **Rulings (D11):** probes are deterministic HTTP/CLI queries — **never
prompt spend** (an LLM call is not a probe); cached in-memory per provider with TTL
`tiering.probeCacheSeconds` (default **60**, GR2043 if ≤ 0), probed lazily at resolution and
at run start, with a small hard per-probe timeout (seconds) so resolution never stalls;
observe-only (not journaled — they surface in `decisions[]` context, threshold prompts, and
the new **`guardrails providers status`** command, which prints each block's kind, model,
routing tiers, and current probe state). Feasibility of a *stable* Claude usage probe is an
implementation-time risk — **OD-C**.

## 7. The escalation ladder (Stage 3, #228)

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
- **Last-attempt guarantee (OD-A, ruled IN pending sign-off):** the final budgeted attempt
  always resolves at the **strongest served rung** (jumping intermediate rungs if needed), so
  a task never exhausts its budget without the strongest registered model getting one shot —
  needs-human then honestly means "even the strongest available model could not pass the
  deterministic gate."
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

## 8. Steering + threshold prompts (Stage 3, #231)

### 8.1 Ambient steering is structured, not prose (D12)

v1 ambient steering is **`guardrails run --prefer <blockName|kind>`** (repeatable): candidates
matching a preference sort first *within the served-tier constraint* (§6.2 still holds — a
`--prefer local` run serves `hard` from frontier if no local block declares `hard`; leaning
harder than that is a config edit or an explicit pin, both deliberate). Free-text steering
("lean hard on local right now") requires an LLM to interpret it into routing effects —
invariant 1 says no; it is a **v2 bet** (§10) that would compile prose into this same
structured surface. The epic's intent survives: the human authors `routing.tiers`/`notes`
once, then steers with one flag or a threshold-prompt answer.

### 8.2 Threshold prompts — the `routing` autonomy boundary (D10)

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

## 9. Reconciliations

### 9.1 `maxCostUsd` (§2) — unchanged supremacy

Tiering changes *which* attempts spend, never *how spend is governed*: every attempt's
`costUsd` + `overheadCostUsd` still charge the one cap, which still gates new launches only.
An escalated (pricier) attempt is still subject to it. A #231 interactive decision can **never
raise `maxCostUsd`** — only config/CLI can, before the run. The #231 projection is advisory;
the cap is the deterministic ceiling. No contract change.

### 9.2 Overwatcher (#269) — one owner for tier movement (D16)

Both react to repeated guardrail failure; they must not fight. **The ladder owns tier
movement; the overwatcher never selects models or tiers.** Ordering per attempt: the ladder's
next-rung resolution is computed deterministically FIRST; an overwatcher consult (if
triggered) receives the already-escalated planned resolution in its context and may layer its
existing sanctioned levers (guidance injection, budget grants — including the D5 "more
attempts on the stronger model" grant) on top. The ladder is floor policy (like #94), so it
fires under every `autonomyPolicy` value and even when the overwatcher is absent. A
"pin/adjust this task's tier" overwatcher fix-op is a conceivable **v2** allowlist extension
(a runtime override touching no authored file) — explicitly out of v1.

### 9.3 Journal / provenance (§7, #198, #230)

Per-attempt `provenance` gains additive fields (§12.4): `runner` (block name), `kind`, `tier`
(the rung that resolved), `tierSource` (`task | plan-default | escalated | override`),
`effort`; plus an optional per-attempt `usage { inputTokens, outputTokens }` so a costless
local provider still shows volume. #230 is then pure aggregation: the run summary prints
per-tier subtotals ("hard: 42k tok / $3.12 · easy: 180k tok / $0 · untiered: …"), degrading
to tokens-only where no cost was reported, omitted entirely (like today's cost line) when
nothing recorded either. Absent-not-null throughout; old journals read fine.

### 9.4 Definition drift (§7.2)

`action.tier`/`action.effort` live in `task.json`, which `TaskDefinitionHash` covers whole —
so editing a tier on an already-`succeeded` task flags drift. Accepted (D17): carving
execution-hint fields out of the hash buys ergonomics at the cost of a second hashing rule and
a "which fields are hints" argument forever; the safe-suffix auto-resolve (`autonomyPolicy`)
already makes the halt cheap to clear. KISS.

### 9.5 Multi-wave plans (§14)

Tier fields ride inside `task.json`/frontmatter, so waved plans get tiering for free
(wave-qualified identity untouched). `tiering` config is plan-level (the root
`guardrails.json`), like `promptRunners`.

## 10. Phasing and dependency order

| Phase | Contents | Depends on |
|---|---|---|
| **Stage 1** (`model-tiering-foundation.md`) | #224 registry (`kind`/`effort`/`routing` + GR2037/38/39/40/41/42 validation + sentinel update) ∥ #225 tagging (`action.tier`, frontmatter `tier`, `tiering.defaultTier`, skill doctrine) | this DoR reviewed |
| **Stage 2** (`model-tiering-consumers.md`) | #226 resolution (+ ladder-free precedence, provenance fields) → #227 probes + `providers status` (GR2043) ∥ #229 review check ∥ #230 accounting | Stage 1 |
| **Stage 3** (`model-tiering-dynamic-behavior.md`) | #228 ladder + #231 `--prefer` + threshold prompts (`routing` boundary) | Stage 2 (#226/#227) |
| **#223** (standalone) | `openai-compat` runner class filling the §4.4 seam | Stage 1 (the `kind` seam) + real local endpoint available |
| **v2 bets** | prose-steering compiler → `--prefer`; per-model $ pricing table (until then: tokens-only for costless providers); overwatcher tier-pin fix-op; probe-informed *scheduling* (reordering the ready queue by provider headroom) | — |

Each stage lands its own §12 SSOT deltas + `guardrails-domain-knowledge` updates in the same
change (invariant 4).

## 11. Open decisions for human sign-off

- **OD-A — last-attempt-at-strongest guarantee (§7).** Ruled IN, but it changes the cost
  profile: every eventually-failing task's final attempt is frontier spend. Alternative:
  plain +1-per-failure with no final jump (a low-tagged task with small `retries` may then
  exhaust without frontier ever trying).
- **OD-B — routing-boundary non-interactive `prompt` = proceed-with-status-quo (§8.2).** A
  deliberate carve-out from §2.1's "non-interactive prompt halts". The §2.1 delta (§12.2)
  encodes it; strike it and the boundary halts like every other if you disagree.
- **OD-C — Claude usage-probe feasibility (§6.4).** No stable public usage endpoint is
  guaranteed; sign off that `unknown` degradation is acceptable for the flagship provider at
  v1 (threshold prompts then simply don't fire for it).
- **OD-D — author the rollout as a #254 waved plan (§2.1).** Recommended (dogfoods waves;
  matches the barrier shape); maintainer's call at breakdown time.

## 12. Proposed SSOT deltas (verbatim-appliable at build time — the live SSOT is NOT touched by this PR)

### 12.1 §2 `guardrails.json` — Stage 1 (+`probeCacheSeconds`/`thresholdPercent` consumed in Stages 2–3)

Add a top-level optional block (after `preserveAttemptsForSalvage`):

```jsonc
  "tiering": {                        // OPTIONAL (#201). Tier ROUTING activates iff >=1 runner block declares
                                      //   `routing` (below); this block only holds the knobs. Absent = defaults.
    "defaultTier": "medium",          // OPTIONAL plan-wide tier for UNTAGGED prompt actions: "easy"|"medium"|"hard"
                                      //   (GR2039 if unrecognized). Absent = an untagged task keeps LEGACY resolution.
    "thresholdPercent": 80,           // #231 routing threshold-prompt trigger (probed provider usage >= this). Default 80.
    "probeCacheSeconds": 60           // #227 probe TTL. Default 60. Non-positive = GR2043.
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

Prose bullets to add under §2: the tiering-activation rule (active iff ≥1 `routing` block;
tags without any routing block = **GR2041 warning**, plan runs as today) and the GR2040 rule
(§12.5).

### 12.2 §2.1 `autonomyPolicy` — Stage 3

- `boundary` enum: `drift` (#274) | `wave` (#254) | `task` (#269) | **`routing` (#231 —
  provider-limit threshold decisions, §9.6)**.
- Add: "**Routing-boundary carve-out (#231):** at a `routing` boundary the non-interactive
  `prompt` default is *apply nothing* — proceed with unchanged routing, loudly logged and
  recorded as `auto-applied` ('default: routing unchanged') — not an exit-2 halt, because the
  status-quo default applies/spends nothing (the invariant guards APPLICATION; declining to
  change routing needs no sanction, and the run remains bounded by `maxCostUsd` +
  `transientPauseBudgetSeconds`). `halt` still halts at the threshold; `auto` applies the
  deterministic highest-headroom recommendation."

### 12.3 §3 `task.json` — Stage 1

In the `action` block after `"model": null,`:

```jsonc
    "tier": null,                    // prompt actions only (#225): "easy"|"medium"|"hard" difficulty tag feeding
                                     //   attempt-launch tier resolution (§9.6); GR2039 if unrecognized. null/absent
                                     //   = inherit tiering.defaultTier (§2), else legacy resolution.
    "effort": null,                  // prompt actions only (#201): per-task thinking-effort override; mirrors
                                     //   `model` exactly (task.json wins; GR2042 shape check; opaque to the harness)
```

Replace the `action.model` resolution-order sentence with: "**`task.json action.model`** (if
set — bypasses tier resolution AND the escalation ladder entirely) **> tier resolution
(§9.6, when an effective tier exists and tiering is active) > `promptRunners.<name>.model` >
the CLI's own default**." Also §4.2: frontmatter gains the optional `tier` key (judge
guardrails; resolution applies, the ladder does not).

### 12.4 §7 journal — Stage 2 (provenance/usage) + Stage 3 (`no-route`)

- `provenance` gains additive optional fields: `"runner"` (resolved block name), `"kind"`,
  `"tier"` (the rung that resolved), `"tierSource"`: `"task" | "plan-default" | "escalated" |
  "override"`, `"effort"`. Absent (never null noise) for script attempts / legacy journals.
- Attempt record gains optional `"usage": { "inputTokens": 0, "outputTokens": 0 }` (additive;
  the tokens-only accounting surface for costless providers, #230).
- Attempt `outcome` enum gains **`no-route`** — resolution found zero registered candidate
  blocks at-or-above the task's rung (a runtime config gap; validation GR2040 normally
  prevents it). Settles needs-human with "register a provider serving tier ≥ R" feedback.
  Probe-exhausted providers do NOT produce `no-route` — they launch and ride the existing
  transient-pause path (§9.6).
- `decisions[]` `boundary` gains `routing` (§12.2).

### 12.5 §9 — Stage 1 seam note + a new §9.6 "Tier routing (model tiering, #201)" — Stages 2–3

§9 intro: note that `FromConfig` switches on `kind` (GR2037 gate) and that `--model`/effort
flags are emitted from the RESOLVED route. New §9.6 documenting, normatively: the precedence
chain (§6.1); candidate selection + never-route-down + nearest-stronger-rung climb (§6.2);
probes-advise-never-gate + the probe cache + `guardrails providers status` (§6.3/6.4); the
escalation ladder rules incl. same-pool budget + last-attempt-at-strongest + actions-only
(§7); `--prefer` + threshold prompts under the `routing` boundary (§8); the ladder-first /
overwatcher-layers-on-top ordering (§9.2). (Content = this DoR's §6–§9, compressed to
contract language.)

### 12.6 Validation summary (also §12.7 for GR text)

GR2009's runner-command probe extends per-kind (an `openai-compat` block probes its endpoint
reachability as a **warning**, mirroring the PATH probe).

## 13. Reserved diagnostic codes — GR2037–GR2043 (next-free marker → GR2044)

Verified against `DiagnosticCodes.cs` at authoring time: **GR2036** (`ExpectedDurationNonPositive`,
issue #331) is the last taken code and the file's marker says GR2037 is next-free. (The epic
briefing said GR2036 — stale; the file wins.) This DoR **reserves the contiguous block
GR2037–GR2043**; the constants + the historical comment discipline below land in
`DiagnosticCodes.cs` at build time, per stage, and the marker line is bumped to GR2044.

| Code | Name | Sev | Stage | Meaning |
|---|---|---|---|---|
| GR2037 | `UnsupportedRunnerKind` | error | 1 | `promptRunners.<name>.kind` unrecognized, or recognized but not implemented in this harness build; message names the value + the supported set (the #223 seam gate — never a silent claude fallback) |
| GR2038 | `MalformedRoutingGuidance` | error | 1 | `routing` block invalid: missing/empty `tiers`, a value outside the tier enum, non-positive `rank`, or wrong types |
| GR2039 | `UnrecognizedTier` | error | 1 | `action.tier`, judge-frontmatter `tier`, or `tiering.defaultTier` not one of `easy\|medium\|hard` |
| GR2040 | `UnservableTier` | error | 1 | tiering active and a USED tier has no routing-enabled block at-or-above it (resolution would have to route weaker than asked) |
| GR2041 | `TieringInert` | warning | 1 | tier tags present but NO block declares `routing` — tags have no effect; plan runs with legacy resolution |
| GR2042 | `EffortInvalid` | error | 1 | a present `effort` (block, override, or `action.effort`) fails the GR2030-style shape check (non-empty, no whitespace/control chars) |
| GR2043 | `RoutingNumericNonPositive` | error | 2 | `tiering.probeCacheSeconds` / `thresholdPercent` present but not a positive value (cf. GR2012/GR2023/GR2036) |

Historical-comment discipline for the build-time edit: "Next-free allocation confirmed at
authoring time of the model-tiering DoR (`docs/plans/13-model-tiering.md`): GR2036
(ExpectedDurationNonPositive) is the last taken; GR2037–GR2043 are the reserved CONTIGUOUS
model-tiering block (#201: #224/#225 take GR2037–GR2042 in Stage 1; #227 takes GR2043 in
Stage 2). CURRENT next-free code: GR2044."

## 14. Worked example

`guardrails.json` (target state; until #223 lands, the `local-kimi` block fails validation
with GR2037 naming `openai-compat` — delete it to run on a claude-only box today):

```jsonc
{
  "version": 1,
  "maxCostUsd": 10.00,
  "tiering": { "defaultTier": "medium", "thresholdPercent": 80 },
  "promptRunners": {
    "default": "sonnet",
    "opus":       { "command": "claude", "model": "claude-opus-4-6", "effort": "high",
                    "routing": { "tiers": ["hard"], "rank": 1,
                                 "notes": "cross-module architecture, retry/journal contract work" } },
    "sonnet":     { "command": "claude", "model": "claude-sonnet-4-5",
                    "routing": { "tiers": ["medium", "hard"], "rank": 2,
                                 "notes": "typical single-module coding; hard fallback when opus is limited" } },
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
| 01 att 2 (guardrail-failed) | **hard** (ladder +1) | opus(r1), sonnet(r2) | **opus** / claude-opus-4-6 / high | tierSource=**escalated** |
| 02 att 1 | hard | opus(r1), sonnet(r2) | **opus** | tierSource=task |
| 03 att 1 | medium (default) | local-kimi, sonnet | **local-kimi** | tierSource=plan-default |
| 04 att 1 | — (pinned) | — (bypasses resolution + ladder) | claude-opus-4-6 | tierSource=override |

With `--prefer local-kimi`, task 02 still resolves to opus (no local block serves `hard` —
never route down). If a probe reports the Claude provider at 85%, the run pauses at the next
launch boundary under `prompt`+TTY with real options; unattended it proceeds unchanged,
loudly, `decisions[]`-recorded. Run summary: `hard: 41k tok / $2.87 · medium: 190k tok / $0.14 ·
untiered: 12k tok / $0.61`.

## 15. Devil's-advocate self-critique

- **Strongest counter — "the ladder makes weak-model retries a cost, not a saving":** a
  mis-tagged easy task burns a cheap attempt AND a frontier attempt (plus, under OD-A, every
  failing task ends with frontier spend), so a badly-tagged plan could cost MORE than no
  tiering. **Response:** the gate certifies cheaply-produced work at zero marginal risk, so
  the expected saving holds whenever the tag is right more often than not; #229 exists to
  push tag quality up before the run; #230 makes the actual split measurable rather than
  assumed; and `maxCostUsd` bounds the downside deterministically. If dogfood numbers show
  ladder churn dominating, the knob to revisit is OD-A — which is exactly why it is
  escalated, not buried.
- **"Registry-in-promptRunners will bloat the block"** — three optional keys, one sentinel,
  zero new files, full reuse of overrides/validation; the alternative (a `providers.json`)
  costs a referential-integrity layer on day one for hypothetical vendors. YAGNI cuts
  toward the block.
- **"Probes-never-gate wastes a doomed API call"** — one bounded call, already handled by the
  shipped pause machinery; the alternative (probe-gated failure) trusts a cached estimate
  over ground truth and duplicates #115. Cheap insurance, correct layering.
- **"Judge-shopping"** — the ladder never touches judge guardrails (§7 scope), and escalation
  only ever moves *stronger*; there is no mechanism to swap a failing judge for a lenient
  one.
- **"Deterministic routing forgoes the `notes` intelligence"** — yes, v1 routing reads only
  `tiers`/`rank`; the prose informs humans and composed prompts. An LLM router is precisely
  what invariant 1 forbids; the v2 prose-steering bet compiles intent into the structured
  surface instead.

## 16. Decisions — RESOLVED in this DoR

D1 Stage-not-Wave terminology (§2.1) · D2 registry = `promptRunners` generalized, no
`providers.json` (§4) · D3 `effort` is NEW, opaque, runner-translated (§4, corrects the
"shipped with #200" misstatement) · D4 discriminator named `kind`, default `"claude"`,
GR2037 honest rejection (§4) · D5 escalated attempts draw from the SAME retry pool (§7) ·
D6 routing guidance = structured `tiers`/`rank` + advisory `notes` prose, hard split (§4) ·
D7 tier enum `easy|medium|hard`, closed, ordered, final for v1 (§5) · D8 probes advise,
never gate; honest failure via shipped pause machinery; `no-route` = defensive config-gap
outcome only (§6.3) · D9 precedence: explicit pin > interactive decision > `--prefer` CLI >
config; tier resolution below explicit, above legacy fallback (§6.1) · D10 threshold
prompts ride `autonomyPolicy` with a new `routing` boundary; no new knob (§8.2) · D11
probes are deterministic, never prompt spend, TTL-cached (60 s), observe-only (§6.4) ·
D12 ambient steering = structured `--prefer`, prose steering deferred to v2 (§8.1) ·
D13 absent `defaultTier` ⇒ untagged tasks keep legacy resolution; tiering activation =
any `routing` block (§5) · D14 an explicitly-pinned task never enters the ladder (§6.1) ·
D15 ladder triggers: logic failures escalate immediately; timeout/max-turns/output-cap get
one same-tier retry with their shipped escalators first (§7) · D16 the ladder owns tier
movement, the overwatcher layers guidance/budget on top and never picks models in v1 (§9.2)
· D17 tier/effort edits stay inside `TaskDefinitionHash` (drift applies; KISS) (§9.4).

## 17. Implementation handoff (after the #106 review of this draft)

1. **Stage 1 — `guardrails-harness-developer`:** `kind`/`effort`/`routing` on
   `RawPromptRunner`(+overrides)/`PromptRunnerConfig`, `tier`/`effort` on `RawAction`,
   `tiering` on `RawRunConfig`; `FromConfig` kind-switch; GR2037–GR2042 in
   `PlanValidator`/`DiagnosticCodes` (+ marker bump); §12.1/12.3 SSOT edits + the
   plan-breakdown `schemas.md` sentinel mirror. `filesTouched:
   src/Guardrails.Core/{Loading,Prompts}/**, docs/plans/02-…, .claude/skills/plan-breakdown/references/schemas.md`.
   ∥ **`guardrails-skill-author`:** plan-breakdown tagging doctrine + quality bar; report
   surface. `filesTouched: .claude/skills/plan-breakdown/**`.
2. **Stage 2 — `guardrails-harness-developer`:** `TierResolver` + `TaskExecutor` wiring
   (replacing the ~1027–1032 two-level fallback), provenance/usage journal fields,
   `IProviderProbe` + cache + `providers status` command (GR2043), run-summary aggregation;
   §12.4/12.5 SSOT edits. ∥ **`guardrails-skill-author`:** guardrails-review
   appropriateness check (graceful pre-tier skip). ∥ **`guardrails-test-author`:**
   resolution matrix (precedence × activation × GR codes), probe-degradation, aggregation
   goldens.
3. **Stage 3 — `guardrails-harness-developer`:** ladder in the attempt loop (journal-derived
   rung, D15 trigger table, OD-A guarantee as signed off), `--prefer`, threshold boundary +
   `routing` `decisions[]`; §12.2 + §9.6 SSOT edits. **`guardrails-test-author`:** ladder
   determinism incl. resume-recompute; unattended-threshold no-hang; never-route-down.
4. **#223 — `guardrails-harness-developer`,** independently once a local endpoint exists:
   the `openai-compat` runner class behind GR2037's gate (§4.4).

Every stage: `guardrails-domain-knowledge` execution-semantics update in the same change.
