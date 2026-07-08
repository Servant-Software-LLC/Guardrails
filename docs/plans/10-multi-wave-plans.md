# Architecture: first-class multi-wave plans (nested layout) — PLANNED (design of record, #254)

> **Status: PLANNED — design of record for maintainer review (the #106 draft-PR loop).** The
> maintainer has decided the shape (nested layout; strict-order waves; the overwatcher adjusts future
> waves between waves) and has approved two framing decisions baked in below: (1) **unify** the
> prompt/halt/auto autonomy knob across drift-resolution (#274 Part C), the overwatcher (#269), and
> inter-wave adjustment (#254) into **one shared policy + one shared reporting surface**; (2) ship the
> **wave-execution skeleton as v1** and defer **auto-heal + overwatcher-driven inter-wave adjustment to
> v2 bets** (v1 only defines the seam they plug into). Per invariant 4, this doc makes NO SSOT change on
> its own: the contract additions here are the **implementation spec** for the SSOT/harness/skill edits,
> which land in the implementing change. The `docs/plans/02-schemas-and-contracts.md` deltas in this same
> branch are authored in **present tense describing the intended contract** (the PR-#294 convention), even
> where a dependency (#274 Part C `driftPolicy`, #280 staging) is still on a branch not yet merged.

---

## The recursive principle (the spine of this document)

> **A wave is a completion unit that participates in the SAME resume + drift + reset model as a task —
> one level up. The system is one recursion: `task ⊂ wave ⊂ plan`.**

- A **task** is the base completion unit: it completes durably (journal `succeeded` + a plan-branch
  `Guardrails-Task:` trailer), it is skipped on rerun once complete, and if its definition changes after
  it completed it gets drift treatment (#274).
- A **wave** is a completion unit *made of* tasks plus its own entry/exit gates: it completes durably (a
  plan-branch `Guardrails-Wave:` marker commit), it is skipped on rerun once complete, and if its
  aggregate definition changes after it completed it gets the SAME drift treatment — one level up.
- A **waved plan** is a strictly-ordered sequence of wave completion units sharing **one run config, one
  continuous plan branch, and one continuous journal**, separated by hard barriers and per-wave
  entry/exit gates.

The symmetry is deliberate and load-bearing: the same predicates govern every level, so wave-level
correctness is *inherited* from the already-correct task level. The single predicate `isCompleted(unit)`
is what cleanly separates **drift** (a change to an already-completed unit → halt/resolve per policy) from
**forward adjustment** (a change to a not-yet-run unit → sanctioned authoring, gated by policy).

---

## What's being asked

GitHub #254 asks to make **"waves" a first-class execution concept** so a plan authored as ordered stages
(Wave 0..N, each building on the *materialized* output of the previous) can be broken down, executed,
resumed, and adjusted as **one cohesive plan** — not a monolithic mega-breakdown (over-sized, and
downstream tasks are undesignable because their evidence references artifacts that don't exist yet), and
not hand-split into separate plans (which loses the inter-wave dependency and the "run it straight
through" intent).

**The maintainer's chosen design (the spec — decided, not re-litigated here):**

1. **Nested layout.** `/plan-breakdown` on a waved plan produces `<plan>/<wave>/tasks/…` — waves are
   ordered subfolders **inside one** plan folder, not separate top-level plans.
2. **Strict total order, no wave-DAG.** The harness runs wave 1, then 2, then 3… A wave never starts until
   the prior wave is fully drained and its exit gate is green.
3. **A task table per wave.**
4. **The overwatcher (#269) adjusts future (not-yet-run) waves between waves**, gated by the shared
   autonomy policy (prompt default / auto opt-in), respecting the non-interactive `Console.IsInputRedirected`
   discipline. *(This is the v2 bet; v1 defines only the seam — see Phasing.)*

**Ambiguity named & narrowed — one blocking naming collision (DECIDED).** `DependencyGraph.Waves()`
already exists and means the DAG's *topological levels* (level 0 = no deps; level N = deepest dep in N−1),
surfaced by `guardrails plan`. The maintainer's "wave" is a coarser, human-authored **plan stage** that
*contains* a task DAG (which itself has topological levels). The word cannot mean both. **Decided: rename
the DAG-level concept to `Tiers()`** (a wave contains tiers), reserving **wave** for the plan stage.

---

## The two baked-in decisions (maintainer-approved)

### Decision 1 — one shared autonomy policy + one shared reporting surface

The prompt/halt/auto knob that appears in **drift-resolution** (#274 Part C, `driftPolicy`, shipping on a
branch), the **overwatcher** (#269), and **inter-wave adjustment** (#254) is unified into **one enum
field** and **one append-only decisions log** discriminated by `boundary: task | wave | drift`. This is
authored as the **shared foundation** here so the #269 draft references it verbatim. Contract in
"Design §10"; SSOT §2 / §7 deltas in the same branch. `driftPolicy` (Part C) is the first instance, folded
in (alias/rename — cheap, unbuilt) when #254/#269 land.

### Decision 2 — v1 = the wave-execution skeleton; auto-heal + overwatcher inter-wave adjustment = v2 bets

**v1 skeleton (this design):** nested layout detection, strict-order wave loop + hard barrier, per-wave
entry/exit gates, per-wave task table, wave-qualified identity, continuous plan branch + cross-wave resume,
wave-level drift (`WaveDefinitionHash`), the recursive `task ⊂ wave ⊂ plan` completion-unit model, and
wave-scoped reset. **Between waves in v1 = a plain human JIT-breakdown/review checkpoint** — proceed
automatically if the next wave is authored+reviewed; **honest halt** (exit 2) with instructions if it is
not. **v2 bets (deferred, clearly marked in the roadmap):** overwatcher-**driven** intelligent inter-wave
adjustment (prompt/auto via the shared policy) and bounded auto-heal. v1 must only **define the seam** they
plug into (the between-wave decision point + the shared autonomy policy + the decisions log).

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

| Concern | Scope | Placement |
|---|---|---|
| Nested-layout **detection** + waved loader/validator (wave dirs, ordering, no-root-`tasks/`, cross-wave-edge ban, wave-qualified identity uniqueness) | plan structure | **harness + schema** — new SSOT section + GR2032–GR2034 |
| **Wave loop + hard barrier** wrapping the existing single-DAG Scheduler; per-wave entry preflight + exit terminal gate | execution | **harness** — thin orchestration above `Scheduler`; reuses the four-folder phases per wave |
| **Wave-qualified task identity** (journal keys, `Guardrails-Task:` trailer, state single-writer key, `dependsOn` scoping) | contract | **harness + schema** — the highest-risk delta (§6.2 / §7 / §5.3) |
| **`Guardrails-Wave:` marker commit + `WaveDefinitionHash`** + continuous plan branch + cross-wave resume + per-wave phase markers | execution | **harness + schema** — additive §7 journal sections + §5.3 trailer |
| **Wave-level drift** (halt/resolve) via the shared policy | contract | **harness + schema** — §7.2 `WaveDefinitionHash`, §7 wave-drift branch |
| **Unified autonomy policy + decisions log** (`boundary` discriminator) | contract | **harness + schema** — §2 field + §7 `decisions[]`; the shared foundation for #269/#274 |
| Per-wave **baseline / review marker / PlanDefinitionHash / diagram** | contract | **reuse** — existing per-folder commands pointed at `<plan>/<wave>/`; near-zero new code |
| Waved **breakdown** + JIT staged (per-wave-after-materialization) mode; per-wave adversarial review | authoring | **skill** — `plan-breakdown` / `guardrails-review` |
| Overwatcher-**driven** intelligent inter-wave adjustment; bounded auto-heal | supervision | **v2 bets (deferred)** — plug into the v1 seam once #269 has its own design |
| A **DAG of waves** / the overwatcher inserting or reordering waves | — | **out of scope** — maintainer decided strict total order |

This is a **v1-shaped skeleton** (builds directly on the just-landed four-folder model,
`09-preflight-first-class`) **plus two named v2 bets**, not a new architecture.

---

## Invariants in play

1. **#2 — Harness is the single writer of merged state.** *The* load-bearing delta: under nesting two
   waves may each author a `01-…` task, so the state single-writer key, the journal key, and the resume
   trailer become **wave-qualified** (`<waveDir>/<taskFolder>`). This generalizes §6.2's "key == own folder
   name" rule and touches the most-invariant part of the system.
2. **#5 — Honest halts.** The between-wave JIT checkpoint *is* an honest halt: when the next wave isn't yet
   broken down/reviewed, the harness halts (exit 2) with instructions rather than fabricating tasks.
   Wave-level drift halts honestly, exactly like task-level drift.
3. **#1 — Deterministic gates over prompt-judges.** Wave entry preflights and exit gates are the existing
   deterministic four-folder gates. The overwatcher (v2) may adjust *future* action prompts / guardrail
   authoring but **never softens a wave gate's verdict** (#269's asymmetry, preserved at the wave boundary).
4. **#6 — Worktree isolation.** The plan branch is **continuous across waves**; wave N's materialized
   outputs live on the integration worktree, **not** the user's read-only checkout — which forces a
   specific answer to "break down wave N+1 against materialized upstream" (Open Decision D).
5. **#4 — SSOT is the schema SSOT.** Every contract delta lands in `02-schemas-and-contracts.md` in the
   same change that implements it. The deltas in this branch are the spec.

---

## Decided (recommended defaults, stated inline)

These were flagged as open in the design report; the recommended defaults are **adopted** here (each still
listed in "Open decisions for review" so the maintainer can override):

- **`Waves()` → `Tiers()` rename.** DECIDED — frees "wave" for the plan stage.
- **Wave completion = a plan-branch marker commit** (`Guardrails-Wave:` / `Guardrails-Wave-Hash:`).
  DECIDED — full task↔wave symmetry, durable across journal loss, a clean Part C rewind boundary. (The
  lighter derived-only alternative is noted for review.)
- **Unify `driftPolicy` + forward-adjustment + overwatcher into one autonomy policy.** DECIDED (maintainer
  Decision 1).
- **Conservative once-per-transition triggers** (the overwatcher's "attempt ≥ 2 / identical-failure"
  firing rules) **belong to #269**, not here. DECIDED — out of scope for #254.

---

## Design

### 1. Nested layout, detection, GR codes

**Layout (waved plan):**
```
plan-name/
├── guardrails.json                 # ONE run config for the whole plan (shared; no per-wave config in v1)
├── preflights/                     # OPTIONAL whole-run Full Flight Checks (once, before wave 1)
├── guardrails/                     # OPTIONAL whole-plan Terminal Gate (once, after last wave) — additive
├── state/                          # ONE continuous journal/state/review for the whole run
├── logs/<runId>/…                  # continuous
├── diagram.md / diagram.html       # OPTIONAL plan-level "wave map" (linear chain of wave containers)
└── wave-01-<slug>/                 # a wave = a mini-plan folder
    ├── preflights/                 #   wave ENTRY gate  ("prior wave's outputs materialized")
    ├── guardrails/                 #   wave EXIT gate   ("this wave's postconditions; releases next wave")
    ├── guardrails.baseline         #   OPTIONAL, per-wave (§11)
    ├── diagram.md / diagram.html    #   per-wave diagram
    ├── state/guardrails-review.json #   OPTIONAL, per-wave review marker (§13)
    └── tasks/<NN-verb-object>/…    #   the wave's task DAG
    wave-02-<slug>/ …
```

**Detection (the discriminator).** A plan folder is *waved* iff it has **no root `tasks/`** AND has ≥1
immediate subdirectory matching `^wave-([0-9]+)-[a-z0-9-]+$`. A flat plan has root `tasks/`. The numeric
group drives the **strict total order** — here `NN` is **load-bearing** (unlike the advisory `NN-` on task
folders), because there is no `dependsOnWave` edge.

**New GR codes** (reconciled with Part C — see "Contract / SSOT impact"):
- **GR2031 — invalid autonomy-policy value** (the unified policy, §10; *subsumes* Part C's driftPolicy-invalid GR2031).
- **GR2032 — mixed layout**: both a root `tasks/` and `wave-*/` subdirectories (error).
- **GR2033 — wave numbering**: duplicate `NN`, or a non-conforming subdirectory alongside wave dirs (error); a numbering **gap** is a warning, not an error.
- **GR2034 — cross-wave `dependsOn`**: a task edge that names a task in another wave (error — cross-wave ordering is the barrier's job).

### 2. Wave-qualified identity (the load-bearing delta — §6.2 / §7 / §5.3)

In a waved plan a task's **canonical id is `<waveDir>/<taskFolder>`** (e.g.
`wave-02-provision/01-author-tests`). This value appears in:
- the journal `tasks{}` keys and the `Guardrails-Task:` trailer (§7 / §5.3) — so the continuous plan
  branch's resume record is unambiguous across waves that each reuse `01-` numbering;
- the **state-fragment single-writer key** (§6.2) — a fragment's top-level key must equal the task's
  wave-qualified id; a bare `01-author-tests` is rejected as foreign, exactly as a `stableId`-keyed
  fragment is today (#164).

**`dependsOn` is intra-wave only.** A task references siblings **within its own wave** by plain folder
name. A cross-wave `dependsOn` edge is a **GR2034** error — cross-wave ordering is the wave barrier, not a
task edge. Each wave's DAG is therefore self-contained ("no DAG of waves" enforced structurally).

**Cross-wave state reads (GR2022 delta).** A wave-N+1 task legitimately reads wave-N's produced state via
the wave-qualified key `$state['wave-01-…/03-generate']`. GR2022 ("a cross-task state read needs a
`dependsOn` ancestor or a seed key") gains a **wave-aware branch**: a referenced id in an **earlier wave**
is satisfied by the barrier (it provably ran); a **same-wave** id still needs the `dependsOn` edge; a
**later-wave** id is an error (not yet run). The zero-false-positive property is preserved.

### 3. The wave scope of the four-folder model (§3.3)

The two-scope model (plan-level + task-level) gains a **middle wave scope** by applying the same folder
mechanism at wave granularity:

| Scope | Preflight folder | Guardrail folder | Runs |
|---|---|---|---|
| **Plan (whole run)** | `<plan>/preflights/` (optional) | `<plan>/guardrails/` (optional, additive) | preflights once before wave 1; guardrails once after last wave |
| **Wave (per stage) — NEW** | `<plan>/<wave>/preflights/` = **entry gate** | `<plan>/<wave>/guardrails/` = **exit / terminal gate** | entry before the wave's DAG (against plan-branch HEAD = materialized prior wave); exit at wave end on merged HEAD-so-far |
| **Task** | `tasks/<id>/preflights/` | `tasks/<id>/guardrails/` | per task, unchanged |

**"Terminal-gate-of-wave-N == preflight-of-wave-(N+1)"** (the issue's central observation) is realized
precisely: wave N's exit gate certifies the merged HEAD; wave N+1's entry preflight verifies that same HEAD
carries the artifacts it depends on. Two authored folders sharing one boundary — no new construct.

**GR2028 applies per wave.** Any multi-leaf/fan-in wave's `<plan>/<wave>/guardrails/` must carry ≥1 *real*
integration re-run (build / suite / union-invariant — content-checked, not presence). The **last wave's
exit gate runs on the fully-merged HEAD** (the plan branch is continuous; the last wave integrates last),
so it **is** the whole-plan terminal soundness boundary. A plan-root `<plan>/guardrails/` is therefore
**optional-additive** (a whole-plan final gate for checks meaningful only once everything is done).
`catches:`/GR2027 and the one shared guardrail-file parser apply to all six folder instances unchanged.

### 4. Wave loop + hard barrier (the minimal scheduler delta)

**A wave is a partition of the task DAG with a hard barrier.** The delta is a **thin wave loop above the
existing Scheduler**; the Channel-based Scheduler's internals (workers, `maxParallelism`, retry,
needs-human/blocked, per-task resume pre-pass, integration/settle) are **unchanged**. Per wave, in strict
order:

1. skip if the wave is already complete (resume, §6);
2. **[JIT checkpoint — v1]** if the wave folder is absent/empty/unreviewed → **honest halt** (exit 2) with
   breakdown instructions (pointed at the integration worktree, Open Decision D). *(v2: the overwatcher
   between-wave step plugs in here.)*
3. run the wave **entry preflight** (the plan-preflight phase, scoped to the wave; skip-once-per-hash);
4. build the wave's `DependencyGraph` over **that wave's tasks only** and drain it on the **continuous plan
   branch** via the existing Scheduler;
5. **HARD BARRIER:** wait for full drain. Any `needs-human`/`blocked` → the run halts at this wave; later
   waves never start;
6. run the wave **exit / terminal gate** (the plan-guardrail phase, scoped to the wave); fail → halt;
7. **write the wave-completion marker commit** (§5) + journal the wave complete;
8. next wave.

The four-folder plan-level phases (`09-preflight-first-class`) are **invoked once per wave subfolder**
instead of once per plan — the wave loop is essentially "run the existing plan pipeline N times on N
subfolders sharing one branch/journal, with a barrier + gates between." That reuse is the KISS payoff.

### 5. The recursive completion-unit model — durable wave completion + `WaveDefinitionHash`

**Durable "wave completed" record — DECIDED: a plan-branch marker commit (mirrors a task's trailers),
while remaining reconstructible from its tasks.**
- **Wave-completed predicate** = *every task in the wave has a green durable record* (journal `succeeded`,
  corroborated by its wave-qualified `Guardrails-Task:` trailer) **AND** *the wave's exit-gate phase marker
  is `passed` for the wave's current hash*.
- **Durable anchor:** on a wave settling complete, the harness writes an **empty marker commit on the plan
  branch** carrying `Guardrails-Wave: <waveDir>` / `Guardrails-Wave-Hash: <WaveDefinitionHash>` /
  `Guardrails-Run: <runId>` — the wave-level analogue of the task integration commit's trailer triple
  (§5.3). Internal `--no-verify` bookkeeping commit; backward-compatibly omitted when unavailable.
- **Why a marker commit (not derived-only):** survives `run.json` loss like a task trailer; gives Part C a
  single unambiguous rewind boundary ("reset to the predecessor wave's marker"); durably anchors the
  wave-gate-folder portion of the hash no per-task trailer covers. (Derived-only is the lighter alternative
  — Open Decision E.)

**`WaveDefinitionHash` — composition (nests between `PlanDefinitionHash` and `TaskDefinitionHash`).**
`sha256:` over labeled segments, same discipline as `PlanHash`/`TaskDefinitionHash`:
1. for each task in the wave (wave-relative task-id ordinal order): **the task's `TaskDefinitionHash` value
   itself** — folding the child hashes, not re-reading task files;
2. every file under `<plan>/<wave>/preflights/**` (recursive, sorted, newline-normalized);
3. every file under `<plan>/<wave>/guardrails/**` (recursive, sorted, newline-normalized).

Folding the child hashes **guarantees the wave hash changes iff a constituent task hash changes or a
wave-gate file changes** — the levels cannot drift apart, and it transitively reuses the same
`TaskDefinitionFiles` primitive #260/#274 share. The nesting: `PlanDefinitionHash` ⊇ (per-wave)
`WaveDefinitionHash` ⊇ (per-task) `TaskDefinitionHash`.

### 6. Cross-wave resume

One continuous journal, one continuous plan branch. **Resume algorithm:** iterate waves in order; a wave
whose tasks are all `succeeded` **and** whose exit-gate marker is `passed` for the current wave hash
**skips entirely**; the first wave failing that test is the **resume-target** — run its entry preflight
(skip-once-per-hash), resume its DAG via the existing per-task pre-pass (trailer + journal), run its exit
gate; then continue.

**Per-wave phase markers** (additive §7 journal keys) mirror the existing `planPreflights` /
`planGuardrails` semantics exactly, one instance per wave:
- wave **entry preflight** → skip-once-per-`planHash(wave)` (same negative-baseline reasoning as the
  pre-DAG rule: an entry check like "artifact X not yet present" is true only at the wave's true start);
- wave **exit gate** → always re-evaluates the current HEAD (like the terminal gate); terminal-only resume
  (B2(b)) applies per wave.

**Wave-drift branch of the pre-pass** (the recursion applied to resume). For every wave the pre-pass is
about to mark **completed-and-skip**, compute its current `WaveDefinitionHash` and compare to the recorded
one (journal, or the `Guardrails-Wave-Hash:` trailer): **absent** (upgrade) → assume-unchanged → skip;
**match** → durable skip; **mismatch on a COMPLETED wave** → wave-level drift → **halt / prompt / auto per
the autonomy policy** (§10), reported as a wave-granularity `DefinitionDrift` entry (wave id, old→new short
hash, which constituent tasks drifted by their `TaskDefinitionHash`, which wave-gate files changed, the
transitive downstream waves that will re-run, and the two remediation paths).

### 7. Drift vs forward-adjustment — the `isCompleted` predicate (THE key distinction)

Drift is defined **strictly over COMPLETED units**. The single predicate `isCompleted(unit)` governs both
levels and both events:

| Event | Classification | Governed by | Drift? |
|---|---|---|---|
| Edit a **pending** task | authoring | (nothing — just runs) | **No** |
| Edit a **completed (green)** task | task drift (#274) | autonomy policy | **Yes → halt/resolve** |
| Adjust an **unrun (all-pending)** future wave between waves | **sanctioned forward adjustment** | autonomy policy | **No** |
| A **completed wave**'s definition changed since it ran | wave drift (this design) | autonomy policy | **Yes → halt/resolve** |

> **Drift ⟺ the changed unit was already COMPLETED. Forward adjustment only ever touches all-`pending`
> units, therefore it is never drift; a change to a completed unit is always drift.**

This closes the two failure modes: (a) a spurious halt on every legitimate forward adjustment — impossible,
because forward adjustment changes no completed unit; (b) silent reuse of a drifted completed wave —
impossible, because any change to a completed unit trips the corresponding drift check. A **partially-run**
wave composes the two granularities without ambiguity: it is not wave-completed (so wave-drift doesn't
apply), its individual **green tasks are still task-drift-protected** (#274), its pending tasks are freely
editable; the overwatcher (v2) never touches a partially-run wave (it adjusts only fully-`pending` future
waves), and the current halted wave is handled by the ordinary task-level needs-human/resume flow.

### 8. Wave-scoped reset / `--fresh` (Part B teardown + Part C scoped rewind, one level up)

- **`--fresh` (Part B)** tears down the **whole plan branch** — all waves, all `Guardrails-Wave:` markers,
  all `Guardrails-Task:` commits, the integration worktree — and re-seeds. Inherently whole-plan (task-level
  `--fresh` already tears down the plan branch, §6.1).
- **`guardrails reset <plan> <wave>/<taskId>`** — task-scoped rewind: that task + its in-wave descendants +
  all later waves (they built on it). Existing Part C, wave-qualified id.
- **`guardrails reset <plan> <wave>`** *(new — the wave-level mirror)* — wave-scoped rewind: every task in
  the wave + rewind the plan branch to the predecessor wave's marker (user HEAD for wave 1), re-running that
  wave + all downstream waves.
- **Bonus soundness property from strict order.** Because waves are a strict total order with **no
  cross-wave fan-in**, a wave-scoped rewind is **always** a safe trailing suffix of plan-branch history. The
  fan-in-descendant unsoundness that forces *task-level* Part C to sometimes halt-rather-than-auto-resolve
  **cannot arise across waves** — so wave-granularity Part C auto-resolve is **unconditionally sound**. (A
  genuine dividend of the maintainer's strict-order decision.)

| Concern | Task level (#274) | Wave level (this design) |
|---|---|---|
| Durable completion record | `succeeded` + `Guardrails-Task:` trailer | wave-completed predicate + `Guardrails-Wave:` marker commit |
| Recorded definition hash | `TaskDefinitionHash` (`Guardrails-Task-Hash:`) | `WaveDefinitionHash` (`Guardrails-Wave-Hash:`), folds the task hashes |
| Skipped on rerun when unchanged | yes | yes |
| Drift of a completed unit | halt/resolve per policy | halt/resolve per policy |
| Change to a not-yet-run unit | authoring (not drift) | sanctioned forward adjustment (not drift) |
| Part C scoped rewind | task + descendants (halts on unsafe fan-in) | wave + downstream waves (**always safe suffix**) |
| Part B full teardown | `--fresh` / `reset -y` | same |

### 9. Per-wave diagram + task table

- **Per-wave diagram:** each wave subfolder is a mini-plan, so `guardrails graph <plan>/<wave>` renders
  that wave's container-model diagram **unchanged** (its Full-Flight-Checks / Terminal-Gate brackets = the
  wave's entry/exit folders).
- **Plan-level "wave map"** (recommended, low priority in v1): a coarse top-level `diagram.md` — a linear
  chain `wave-01 → wave-02 → wave-03` of wave-containers, each `click`-linking to its wave diagram. Reuses
  the container/style machinery (a wave is a container with a `style` fill and an edge to the next).
- **Per-wave task table (#3):** a run-UI / `IRunObserver` concern, **no new contract**. The wave loop emits
  `WaveStarted`/`WaveCompleted` observer events; the existing Spectre live table is retitled/reset per wave
  (completed waves summarized). Composes with the in-flight `diagram-live-status-and-search` work.

### 10. The unified autonomy policy + shared reporting surface (shared foundation for #269 / #274)

**One enum field** governs every prompt/halt/auto decision boundary. **Recommended name: `autonomyPolicy`**
(the exact name is Open Decision A). Values and semantics (authored so #269 reuses them verbatim):

- **`prompt` (default)** — at a decision boundary, if stdin is an interactive TTY, present the details and
  ask for approval; apply on approval, halt on decline. If **non-interactive** (`Console.IsInputRedirected`),
  do NOT block — **halt honestly** (exit 2) with the same details for out-of-band review. (The maintainer's
  "prompt-TTY default" + the `ResetCommand.Confirm`/`IsInputRedirected` discipline.)
- **`halt`** — never prompt, never auto — always halt (exit 2) at the boundary for out-of-band human action.
  The most conservative. (Part C's current `driftPolicy: "halt"` default behavior.)
- **`auto`** ("just handle everything") — apply the decision without prompting wherever it is SAFE /
  SANCTIONED; an UNSAFE / UNSOUND action still halts regardless of policy. (Part C's `reprocess` + the
  maintainer's "just handle everything" for inter-wave.)

**Load-bearing invariant across all three values:** `auto` authorizes **SPEND / APPLICATION of a SAFE
action, never an UNSOUND one.** An unsound boundary (e.g. a task-level fan-in-descendant drift rewind)
**always halts regardless of policy** — this is exactly Part C's "the flag authorizes SPEND, never an
UNSOUND rewind." (Wave-level rewind is always safe, §8, so `auto` always applies it.)

**Folding in `driftPolicy` (Part C).** Part C ships `driftPolicy: "halt" (default) | "reprocess"`. Under the
unified policy: `halt` → `halt`; `reprocess` → `auto`; the new middle value `prompt` becomes the unified
default. This **changes Part C's effective default from `halt` to `prompt`** — a merge-time reconciliation
flag for the maintainer (cheap; Part C is unbuilt). Non-interactive `prompt` degrades to `halt`, so CI
drift still halts (safe); interactive `prompt` requires approval before any auto-resolve (safe). The
`--reprocess-drift` CLI flag becomes an alias for `--autonomy auto` (or is retired).

**Shared reporting surface — the decisions log (`boundary` discriminator).** A per-run append-only record of
every autonomy-policy decision point, canonical store = an additive top-level `decisions[]` array in
`run.json` (§7), each entry:

```jsonc
{
  "boundary": "wave",              // task | wave | drift  — the decision-class discriminator (extensible)
  "policy": "prompt",              // the autonomyPolicy value in force at this boundary
  "decision": "prompted-approved", // halted | prompted-approved | prompted-declined | auto-applied
  "at": "2026-07-08T14:03:11Z",
  "subject": "wave-02-provision",  // the unit the decision concerned (task id / wave dir / drifted unit)
  "headline": "…",                 // one-line human summary
  "detail": "…"                    // fuller detail (the drift breakdown, the adjustment summary, …)
}
```

- `boundary: "drift"` — a definition-drift resolution (Part C, task or wave granularity).
- `boundary: "wave"` — a wave-boundary decision (#254 inter-wave; wave completion/drift).
- `boundary: "task"` — a task-level autonomy decision (#269 overwatcher per-task attempts-vs-fix-vs-halt).

**Rendering:** the CLI renders new decisions **under the live task table** (reusing `IRunObserver`, the #269
"under the task table" requirement) and the static log site (§12) surfaces the log. The canonical durable
store is `run.json` `decisions[]`; an optional human-readable `logs/<runId>/decisions.md` mirror may be
written on the fly.

**What ships in v1 vs v2 for this shared foundation:** the field + the decisions log + the `prompt`/`halt`
paths ship in **v1** (consumed by wave-level drift resolution and the between-wave JIT checkpoint —
proceed-if-ready-else-honest-halt). `auto` intelligent inter-wave *adjustment* (the overwatcher authoring
changes to a future wave) is the **v2 bet**; v1 defines the seam (the between-wave decision point) it plugs
into.

---

## Phasing — v1 skeleton vs v2 bets

**v1 — the wave-execution skeleton (this design; a post-v1 fast-follow on the roadmap).**
Nested-layout detection + waved loader/validator (GR2031–GR2034); the wave loop + hard barrier; per-wave
entry preflight + exit terminal gate (reusing the four-folder phases per subfolder); wave-qualified identity
(§6.2/§7/§5.3) + GR2022 wave branch; continuous plan branch + cross-wave resume + per-wave phase markers +
`Guardrails-Wave:` marker commit + `WaveDefinitionHash`; the recursive completion-unit model + wave-level
drift; wave-scoped reset; per-wave diagrams + per-wave task table; the shared autonomy policy + decisions
log (`prompt`/`halt` paths). **Between waves = a plain human JIT-breakdown/review checkpoint** — proceed if
the next wave is authored+reviewed, else honest halt with instructions. Delivers multi-wave value with the
human doing between-wave adjustment manually; "run straight through" is preserved for fully pre-authored,
pre-reviewed plans.

**v2 bet — overwatcher-driven intelligent inter-wave adjustment.** The overwatcher inspects a completed
wave's actual outcome and **proposes adjustments to the next (all-pending) wave's tasks/guardrails**, gated
by `autonomyPolicy` (prompt with details / auto), re-staling that wave's per-wave review marker. Plugs into
the v1 between-wave seam. **Gated on #269 landing** — and **#269 needs its own design of record first**
(trigger set, decision authority, self-heal-vs-determinism asymmetry, tiering, reporting surface are all
unshaped). #269's draft reuses the shared autonomy policy + decisions log authored here verbatim.

**v2 bet — bounded auto-heal.** The overwatcher's authority to fix an authoring defect (an action prompt, a
config value) and resume, within the determinism guard (never a guardrail assertion). #269 territory.

---

## Devil's-advocate self-critique

**C1 — Per-wave-vs-plan-root contract duplication (the prior pass's strongest point).** *Baseline, review
marker, PlanDefinitionHash, preflight/terminal gate, diagram — every plan-level contract now has a per-wave
twin. "A wave is a separate plan" gets these for free; nesting re-implements them.*
**Response:** It **re-uses**, it does not re-implement. Each is already a function *of a folder*, and the
commands (`lock`, `merge`, `mark-reviewed`, `graph`, `validate`) already take a folder argument; pointing
them at `<plan>/<wave>/` is near-zero new code. What is genuinely new is only the wave loop, wave-qualified
identity, the continuous branch, and the boundary policy. The "separate plans" alternative gets folder
machinery free **but loses** the continuous plan branch, the single run config, the single journal/resume,
and modeled inter-wave ordering — i.e. it *is* pain point #2 in the issue. The maintainer's trade is sound;
the honest residual is C2, not duplication.

**C2 — Wave-qualified identity is a real change to the single-writer-of-state invariant.** The §6.2 key rule
is the most load-bearing anti-poisoning contract in the system; this generalizes "key == folder name" to
"key == wave-qualified path," rippling into the journal key, trailer, `dependsOn` scoping, GR2022, and
`reset`/`--revalidate-task` id parsing.
**Response:** It is a **namespacing generalization, not a loosening** — the key still uniquely identifies one
writer, gaining a wave segment. **Mitigation:** treat it as the first and most heavily-tested implementation
slice, with a fuzz/property test that no two waves' fragments can collide (mirroring the existing
single-writer tests). Flagged as the highest-risk item in the handoff.

**C3 — Drift-halt vs intentional overwatcher adjustment.** *Won't editing a future wave trip the drift-halt
that exists to catch definition changes?*
**Response:** No — by construction (Design §7). The drift-halt fires only for already-COMPLETED units; the
overwatcher's authority is restricted to all-`pending` future waves. Disjoint domains, resolved by one
shared `isCompleted` predicate — no exception carved into the drift-halt itself (which would have been the
dangerous design).

**C4 — Materialized upstream lives on the plan branch, not the user's checkout (the sharpest genuine
tension).** JIT breakdown of wave N+1 must see wave N's outputs, which under worktree isolation are on the
integration worktree, never the read-only checkout, until the whole run finishes.
**Response:** The between-wave checkpoint **points the author at the integration worktree** (`_integration`
under the worktree root) — the #197 "hand-fix a merged workspace file" flow, consistent, not a workaround.
It adds real friction, so an opt-in **per-wave materialization to the checkout** is Open Decision D — named,
not silently assumed.

**C5 — The wave barrier destroys cross-wave parallelism.** A wave-3 task with no true dependency on wave-2
still waits for the wave-2 barrier.
**Response:** Inherent and **accepted** — the maintainer's explicit choice (strict order, no wave-DAG).
Skill guidance: for fine-grained parallelism, model it as one wave with a task DAG; waves are the *coarse*
ordering for stages whose downstream tasks can't be authored until upstream is real.

**C6 — Introducing shared-policy / marker-commit machinery before it is fully used (YAGNI).**
**Response:** The autonomy policy IS consumed in v1 (wave-drift resolution + the JIT checkpoint), so it is
not premature; only the `auto` intelligent-adjustment consumer is deferred (v2). The marker commit IS
consumed in v1 (durable wave completion + wave-drift anchor + wave-scoped rewind boundary). Both earn their
place in v1; the derived-only lighter alternative for the marker is offered (Open Decision E).

---

## Contract / SSOT impact (the deltas authored in this branch)

All in `docs/plans/02-schemas-and-contracts.md`, present-tense, same branch:
- **New section "Multi-wave plans"** — layout, detection, wave-qualified identity, the wave scope, the wave
  loop + barrier, the recursive completion-unit model, `WaveDefinitionHash`, cross-wave resume,
  drift-vs-forward-adjustment, wave-scoped reset, wave map.
- **§2** — the unified **`autonomyPolicy`** field (`prompt` default / `halt` / `auto`) + GR2031; the shared
  foundation the #269/#274 drafts reference.
- **§3.3** — per-wave GR2028; the plan-root terminal gate becomes optional-additive for waved plans.
- **§5.3** — the `Guardrails-Wave:` / `Guardrails-Wave-Hash:` marker-commit trailers; the `Guardrails-Task:`
  trailer carries the wave-qualified id.
- **§6.2** — the wave-qualified single-writer key; **GR2022** wave-aware branch.
- **§7** — per-wave `waves[]` completion/hash record; the top-level `decisions[]` reporting surface
  (`boundary` discriminator); the cross-wave resume rule; the wave-drift resume branch.
- **§7.2 / §7.3** — `WaveDefinitionHash` nesting between `PlanDefinitionHash` and `TaskDefinitionHash`; the
  wave-granularity `DefinitionDrift` entry.
- **§10** — the plan-level wave map.
- **§11** — per-wave `guardrails.baseline`.
- **§13** — per-wave review marker + per-wave `PlanDefinitionHash`.

**GR-code reconciliation with Part C (#274).** On `master`, GR2030 is the last taken code (GR2031
next-free). Part C's branch claims **GR2031 for an invalid `driftPolicy` value**. Because Decision 1 folds
`driftPolicy` into the unified `autonomyPolicy`, Part C's GR2031 and this design's "invalid autonomy-policy
value" are the **same check generalized** — they collapse to **one GR2031**, no renumbering needed. This
design's genuinely-new codes are **GR2032** (mixed layout), **GR2033** (wave numbering), **GR2034**
(cross-wave `dependsOn`). Keep Part C and #254 consistent at merge by treating GR2031 as the unified-policy
code in both.

---

## Open decisions for review (maintainer's call)

- **A. The unified policy NAME.** Recommend **`autonomyPolicy`** (values `prompt`/`halt`/`auto`). Alternatives:
  `interventionPolicy`, `supervisionPolicy`, `automationPolicy`.
- **B. Plan-root terminal gate for waved plans.** Recommend **optional-additive** (last wave's exit gate is
  the whole-plan boundary) vs disallow a plan-root gate entirely.
- **C. Per-wave `PlanDefinitionHash` scope.** Recommend **exclude the shared `guardrails.json`** (upstream
  review markers stay stable against config/downstream churn) vs include it (a config edit re-stales every
  wave's review, including already-run ones).
- **D. Intermediate-wave materialization.** Recommend **point the author at the integration worktree** (v1,
  mirrors #197) vs add an opt-in **per-wave merge-to-checkout** so each wave lands in the user's checkout
  before the next is authored (removes C4 friction, adds a merge-back per wave).
- **E. Wave completion record.** Recommend **marker commit** (`Guardrails-Wave:` — full durability + a clean
  rewind boundary) vs **derived-only** (reconstruct from per-task trailers + the journal wave marker; lower
  git churn, no wave-gate-folder hash durability).
- **F. Wave-dir convention.** Recommend `^wave-([0-9]+)-[a-z0-9-]+$` with the `NN` driving strict order;
  numbering **gaps = warning**, duplicate/absent = error (GR2033). Confirm the `<wave>` segment appears
  verbatim in the wave-qualified id.
- **G. `driftPolicy` default reconciliation.** Folding `driftPolicy` into `autonomyPolicy` changes Part C's
  effective default from `halt` to `prompt`. Confirm (safe: non-interactive `prompt` degrades to `halt`).

---

## Implementation handoff (agent + filesTouched + sequencing)

After this design of record is approved via the #106 draft-PR loop:

1. **`guardrails-harness-developer` + `guardrails-test-author` (paired), v1 skeleton, SSOT-first.**
   `filesTouched`: `docs/plans/02-schemas-and-contracts.md` (the deltas above — **same change**);
   `src/Guardrails.Core/**` (waved loader/validator, `Waves()→Tiers()` rename, wave loop, wave-qualified
   identity, `WaveDefinitionHash`, marker commit, cross-wave resume, per-wave phase invocation, the
   `autonomyPolicy` field + `decisions[]`); `src/Guardrails.Cli/**` (`run`/`validate`/`plan`/`reset`/
   `--revalidate-task`/`graph` wave-awareness, per-wave table, the decisions rendering); `tests/**`.
   **Test-first priority:** (a) wave-qualified identity + state single-writer no-collision fuzz (C2,
   highest risk); (b) cross-wave resume (skip completed, resume-into-halted); (c) wave-drift +
   drift-vs-forward-adjustment `isCompleted` disjointness; (d) barrier semantics (later wave never starts on
   halt); (e) GR2031–GR2034 + GR2022 wave branch; (f) the autonomy-policy value + non-interactive `prompt`→halt.
2. **`guardrails-skill-author`** — `plan-breakdown` waved output + JIT staged mode (break down wave N+1
   against the integration worktree); `guardrails-review` per-wave; `guardrails-domain-knowledge` +
   `guardrails-dev-knowledge` updates.
3. **[v2, gated on #269's own design of record]** overwatcher-driven inter-wave adjustment + bounded
   auto-heal, plugged into the v1 between-wave seam, reusing the shared autonomy policy + decisions log.
