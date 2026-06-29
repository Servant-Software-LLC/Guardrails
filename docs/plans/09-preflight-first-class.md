# Architecture: Preflight as a first-class citizen — DEFERRED design (#183)

> **Status: DEFERRED design-of-record.** This document is NOT an input to `/plan-breakdown` and
> authorizes NO implementation milestones today. It records (1) the generalized **preflight
> principle**, (2) the **determination** — reached by a 4-lens review (architect /
> devil's-advocate / harness-developer / skill-author) that **unanimously converged** — to ship
> the principle as **DOCTRINE first** and **defer first-class status**, (3) the **PARTITION** of
> the preflight space into three buckets (A shared baseline / B negative attribution / C per-task
> dependency-delivery), only one of which is a candidate for a first-class harness phase, (4) the
> **deferred Bucket-C design** so it is ready if and when the trigger fires, and (5) the explicit
> **trigger criteria** and **BLOCKERs** any Bucket-C work must clear before it may start. The
> contract additions sketched in §"Contract / SSOT impact" are a **forward design**, not yet
> applied to `02-schemas-and-contracts.md` (the SSOT) — they land there only in the change that
> implements Bucket C, never before (invariant 4).
>
> **One-line claim, scoped honestly:** a brownfield task that **modifies** (not creates) a verified
> thing can have its end-of-task gate evaluated *before* the task — a **preflight baseline**. The
> principle is real and ships now as doctrine; it is **fully expressible with existing primitives**
> (a no-op-action ROOT task + a guardrail + a DAG edge), so a schema field / harness phase / new
> exit code would be **machinery for no new behavior** — ceremony — until a real dogfooded case
> proves the doctrine archetype cannot express it. "A prompt may propose, only a deterministic gate
> may certify" — and a preflight is just that gate, run one step earlier to fail fast and attribute
> correctly.
>
> **The PARTITION (the spine of this document).** A team pushback — *"preflight is a PER-TASK
> just-in-time phase (`<task>/preflights/`), not a global pre-DAG run phase"* — was evaluated by the
> 4-lens team and the product owner adopted a **partition**: per-task JIT is reserved for **one
> slice only**; doctrine keeps the rest. The preflight space splits into three buckets, and
> **conflating them is the design error this partition exists to prevent**:
>
> | Bucket | What it claims | Home | Status |
> |---|---|---|---|
> | **A — shared positive baseline** | "this AREA starts from green" (the #181 case) | **doctrine** — a no-op-root TASK, one per area | **shipped** (preview.34); explicitly **NOT** moved per-task |
> | **B — negative / assert-absent baseline** | "the thing this task introduces is ABSENT now" | **one-shot, pre-merge** — doctrine root no-op today; deferred global one-shot if ever first-class | **doctrine**; **FORBIDDEN at per-task scope** (union-inversion) |
> | **C — per-task JIT dependency-delivery precondition** | "did my producer actually deliver the type/route/symbol I build against, in MY forked worktree at `taskBase`, right now" | the ONLY slice that justifies a per-task JIT preflight | **DEFERRED** behind the sharpened trigger; **folder-vs-flag is the open decision** |
>
> Bucket C is the one capability the *global pre-DAG phase* (the shape the earlier draft of this doc
> proposed) **structurally cannot express** — the phase runs before any producer has run, so it can
> never check "did my producer deliver." That, and nothing else, is what a per-task JIT preflight is
> for. Buckets A and B are settled by doctrine and are recorded here so a future reader does not try
> to push them per-task (A has no honest per-task home; B false-halts per-task — both shown below).

---

## What's being asked

GitHub #183 asks: **make "preflight" a first-class citizen** of the harness. The motivating
intuition ("make sure the plane is on the runway before flight"): for a brownfield task that
*changes* something already verified, run that verification **before** the task — so a broken
*starting* state fails fast (no token burn on a doomed attempt) and a post-task failure is
correctly *attributed* to the task's own work rather than to a pre-existing defect.

**#181 is the canonical first instance**: before any task in a brownfield plan runs, verify the
**existing tests in the touched area already pass** (a POSITIVE baseline). If they are already red,
the run halts immediately with an honest "your starting point is broken" — instead of an
implementation task burning its whole retry budget trying to make a green gate that was never
green to begin with.

**The generalized principle (the synthesis this plan formalizes):** *any* brownfield task whose
**end-of-task postcondition guardrail can be evaluated BEFORE the task** — because the task
**MODIFIES / extends** rather than **CREATES** the verified thing — should be able to carry a
**preflight** establishing a baseline. Two polarities, and a third, *temporally distinct* concern:

- **POSITIVE baseline** ("already green / already up") → **fail fast on a broken start.** The thing
  the task will modify is verified to be working *now*; if it is not, halt before spending the task.
  Beyond unit tests: an endpoint already responding, a build already green, a DI registration
  already present, a schema already valid, a route already resolving. This is **Bucket A** when it
  is a property of an AREA shared by ≥2 tasks (the #181 case).
- **NEGATIVE baseline** ("not yet present") → **attribution.** The thing the task will *add* is
  verified to be **absent** now, so a later "it's present" gate is provably the task's own doing,
  not pre-existing. **This polarity already exists** as the `plan-breakdown`
  `tests-fail-on-current-code` / `tests-fail-on-stubs` anti-tautology archetype — a preflight design
  must **cross-reference** it, never fork it. This is **Bucket B**, and its polarity is *inherently a
  "before the whole run" claim* — which is exactly why it is **forbidden at per-task scope** (see
  §"The partition").
- **PER-TASK DEPENDENCY-DELIVERY precondition** ("did my producer deliver the thing I build
  against — in MY worktree, at the moment I run") → **a precondition the positive *area* baseline
  cannot state**, because it is about a value produced *by an earlier task within the run*, not a
  property of the starting repo. This is **Bucket C** — the only slice that needs a per-task,
  just-in-time evaluation, and the only first-class candidate left open.

**Polarity is necessary but not sufficient to place a preflight.** The earlier draft of this doc
treated "positive vs negative" as the whole taxonomy and proposed a single *global pre-DAG phase*
for both. The partition adds the missing axis — **WHEN** the claim is true and **WHOSE** worktree it
is true in — and that axis is what separates A/B (run-global, settled by doctrine) from C
(task-local, JIT, the only first-class candidate).

**Ambiguity named & narrowed.** The word "first-class" is the load-bearing ambiguity, and the
brief leaves three readings open. I narrow them up front so the determination below is unambiguous:

1. **"First-class" = a recognized authoring archetype** (doctrine: a named pattern
   `plan-breakdown` emits and `guardrails-review` probes for). **This is Phase 0 — shipping now**,
   and it is the settled home for **Buckets A and B**.
2. **"First-class" = a harness-acted contract** (a new `<task>/preflights/` folder OR a `task.json`
   flag, with a distinct outcome / exit-code branch). **This is Phase 2 — DEFERRED**, and the
   partition **scopes it down to Bucket C only**: the per-task JIT dependency-delivery precondition.
   The earlier draft's "dedicated *pre-DAG* phase" is **withdrawn** — a pre-DAG phase cannot express
   Bucket C (it runs before any producer), and Buckets A/B do not need it (doctrine covers them).
3. **"First-class" = the harness auto-derives preflights** (it inspects a task and decides "this
   modifies, so inject a baseline"). **REJECTED outright** — "modifies-not-creates" is **undecidable
   by the harness** (see the determination and BLOCKER (b)). Auto-derivation false-fails on every
   legitimate TDD-red / coverage guardrail, which is *designed* to be red before the task.

The brief settles which reading we pursue and when: **(1) now (Buckets A + B), (2) deferred behind a
trigger and scoped to Bucket C only, (3) never.**

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

| Concern | Bucket | Phase | Placement |
|---|---|---|---|
| The **preflight principle** as a named authoring archetype (catalogue entry + insertion rule + a `guardrails-review` probe) | A + B | **Phase 0 (now)** | **skill** — `plan-breakdown` guardrail catalogue + `guardrails-review`; the #181 positive unit-test baseline is the first instance |
| **#181** — positive existing-tests-green baseline for brownfield plans (area-wide start-from-green) | **A** | **Phase 0 (now)** | **skill** — `plan-breakdown` Step-0 scan + a no-op baseline ROOT task, one per area; **no harness change**. Explicitly **NOT** moved per-task (no honest per-task home — §"Bucket A"). |
| **Negative / assert-absent baseline** ("the thing this task adds is absent now") | **B** | **Phase 0 (now)** | **skill** — already IS `tests-fail-on-current-code` / `tests-fail-on-stubs`; a doctrine root no-op one-shot. **FORBIDDEN at per-task scope** (union-inversion — §"Bucket B"). Cross-reference, never fork. |
| **#182** — serial-mode fix to the #174 no-op short-circuit, so the fast red-halt holds in BOTH modes | (supports A) | **Phase 1 (now)** | **harness** — `TaskExecutor` / no-op short-circuit (a bug fix to an existing mechanism, NOT new machinery) |
| First-class **per-task JIT dependency-delivery precondition** — a new `<task>/preflights/` folder **OR** a `task.json` no-burn-precondition flag | **C** | **Phase 2 (DEFERRED)** | **harness + schema** — designed in §"The deferred Bucket-C design"; **folder-vs-flag is the open decision**; lands in the SSOT only when implemented |
| The harness **auto-deriving** pre-applicability ("this task modifies, inject a baseline") | — | **REJECTED** | **out of scope, permanently** — undecidable; false-fails every TDD-red gate |
| A **global pre-DAG preflight phase** for A/B (the earlier draft's shape) | — | **WITHDRAWN** | A/B are settled by doctrine; the phase cannot express C. No global phase is pursued. |
| "Many preflights" volume / cost management | A + C | **constraint** | **skill** — the volume-control "worth-it" gate (§"Volume-control gate"), re-pointed to the partition |

---

## Invariants in play

The determination below is, at bottom, an **invariant argument**. Named, with how each bears:

1. **Deterministic guardrails over prompt-judges; judges never alone.** *Respected and reinforced.*
   A preflight is a *deterministic gate run earlier* — the most deterministic possible use of a
   guardrail (no action, no model, just "does the existing thing verify"). Nothing here adds a
   prompt to a verdict path. The REJECTED auto-derivation reading would have introduced a
   *harness-side judgment* ("does this task modify?") that is not deterministic at all — the
   strongest reason to reject it.
2. **Harness is the single writer of merged state; children get snapshots, write fragments.**
   *Untouched by doctrine; barely strained by Bucket C.* A Bucket-A/B doctrine preflight is a
   no-op-action ROOT task — it writes no fragment, merges nothing, and reads its bytes read-only. The
   Bucket-C per-task precondition runs **inside the consuming task's own segment worktree at
   `taskBase`**, *before* the task's action — it is a **read-only check that produces no fragment and
   no commit** (it precedes the action, which is the only thing that may write a fragment). It never
   touches merged state. Designed to write nothing (§"The deferred Bucket-C design").
3. **Verdicts come from files, never CLI exit codes.** *Respected.* A preflight's pass/fail is a
   guardrail verdict (deterministic exit / prompt verdict file) exactly as any guardrail — the
   preflight is not a new verdict source, just the same source evaluated one step earlier.
4. **`02-schemas-and-contracts.md` is the schema SSOT — a contract change lands there in the SAME
   change that motivates it.** *This is why Bucket C is deferred and its SSOT edits are NOT yet
   applied — and this rewrite makes NO SSOT change.* Doctrine (Buckets A + B) touches **no contract**
   — it is fully expressible with the existing `task.json` + guardrail + `dependsOn` primitives, so
   there is no SSOT edit to make. A Bucket-C contract — *either* a new `<task>/preflights/` folder +
   `TaskOutcome.precondition-failed` + GR2027, *or* a one-line `task.json` no-burn-precondition flag —
   WOULD be an SSOT edit, and per this invariant it lands **only** in the change that implements it,
   never speculatively. Which of the two it is remains the load-bearing open decision; **neither is
   written to the SSOT by this document**.
5. **Honest halts — nothing marked done unverified; needs-human is a feature.** *Respected and
   extended.* A red positive-baseline preflight is the most honest halt there is: "your starting
   point is already broken; I will not pretend my task can fix what it does not own." The fast
   red-halt (Phase 1 / #182) makes that halt *cheap* in both modes rather than a full-budget burn.
6. **Plain files, light setup — no databases, daemons, or SaaS in v1.** *Respected.* Doctrine
   (Buckets A + B) is pure authoring (files in the task folder). Bucket C adds no dependency — it
   reuses the existing segment worktree at `taskBase` and the `IReVerifier` guardrail-runner seam, and
   the **live-probe ban** keeps it to deterministic byte checks (no process/daemon/network).

**The decisive invariant pairing is 1 + 4.** Buckets A and B are fully expressible with existing
primitives (invariant-4 says "no SSOT edit needed → no contract change → it is doctrine"), and the
auto-derivation reading that would add real *capability* on top of them is forbidden by invariant 1
("modifies-not-creates" is a non-deterministic harness judgment). For A and B a first-class field
buys only **polish, not capability** — ceremony. **Bucket C is the one place the partition finds a
capability doctrine cannot express**: a check evaluated *in the consuming task's forked worktree at
the moment it runs*, against a value an *earlier task produced* — which neither a no-op ROOT task
nor a global pre-DAG phase can state. So Bucket C is the only first-class candidate, and even it is
gated, because it is still unproven that it is not just "the consuming task's first guardrail + a
no-burn flag" (the open decision below). Hence: doctrine first (A + B), defer Bucket C behind a
sharpened trigger, build no global phase at all.

---

## The determination (and why) — doctrine first, defer first-class

All four lenses converged on the same decision. The convergent reasoning, recorded so a future
reader does not relitigate it:

1. **It is fully expressible with existing primitives.** A preflight IS: a **no-op-action ROOT
   task** (a task with a trivial always-pass action — e.g. `exit 0` — or no work to do) carrying the
   **baseline guardrail** (the existing-thing-verifies check), with a **`dependsOn` edge** from
   every task that would modify the baselined thing. The DAG already gives "runs first"; the
   guardrail already gives "fail if the baseline is red"; the dependency edge already gives "block
   the dependents." **Nothing in the harness needs to change** to get the behavior. (#181 is exactly
   this: a baseline ROOT task whose guardrail runs the existing touched-area tests, that every
   implementation task depends on.)

2. **A marker the harness does not act on is ceremony** (invariant: no new machinery for no new
   behavior). A `task.json` `"preflight": true` field — or a guardrail `scope` value — that the
   harness merely *records* but does not *schedule differently* buys nothing the no-op-ROOT-task
   already buys for Buckets A and B. It is a label, and a label is not a feature. A first-class
   contract earns its place **only** when the harness does something with it the DAG cannot already
   express — and the partition finds exactly **one** such thing: **Bucket C**, a check run inside the
   consuming task's worktree at `taskBase`, before its action, asserting an earlier task delivered a
   dependency. That is genuinely new behavior (a per-task JIT precondition with fail-fast-no-burn
   semantics), not polish — which is why C, and *only* C, is a live first-class candidate.

3. **"Modifies-not-creates" is UNDECIDABLE by the harness → auto-derivation is REJECTED.** The
   *only* reading of "first-class" that buys genuine capability is the harness inferring
   pre-applicability and injecting baselines itself. It cannot: whether a task *modifies* an existing
   verified thing or *creates* a new one is a semantic property of the task's intent, invisible to
   the harness. Worse, **auto-deriving baselines false-fails on every TDD-red gate**: a
   `tests-fail-on-current-code` guardrail is *designed* to be red before its task; an
   `implement-the-feature` task's coverage guardrail is *designed* to be red before the
   implementation. A harness that "ran the end-gate first to baseline it" would mark every correct
   TDD plan as a broken start. Auto-derivation manufactures a **false-halt class** (red-because-the-
   work-isn't-done-yet read as red-because-broken) and a **vacuous-green class** (baselining a gate
   that is trivially green for the wrong reason). It is rejected, permanently.

4. **The negative baseline already exists — do not fork it.** "Not-yet-present" attribution is
   exactly the `plan-breakdown` anti-tautology archetype `tests-fail-on-current-code` /
   `tests-fail-on-stubs`: prove the new test is red against the *current* code (so a later green
   proves the implementation did it, not a tautological test). A "negative preflight" mechanism would
   be a second name for a shipped pattern — drift, not feature. Any preflight doctrine must
   **cross-reference** the anti-tautology archetype as the negative polarity, not introduce a rival.

**Therefore:** ship the principle as **doctrine** for Buckets A and B (Phase 0), fix the fast-halt in
serial mode (Phase 1), and **defer Bucket C** (Phase 2) behind a sharpened trigger. Build **no global
pre-DAG phase** — A and B do not need it and C cannot use it. Bucket C is deferred not because it is
mere polish (it is real new behavior) but because **it is not yet proven distinct from "the consuming
task's first guardrail + a no-burn flag"** (the F5 reduction, §"The open decision"), and because **no
real dogfooded instance** of it has been captured (§"Trigger"). Absent that artifact, C stays
deferred and the per-task-JIT pushback is recorded as **"considered; scoped to dependency-delivery;
gated."**

---

## The partition (the spine — three buckets, one first-class candidate)

The pushback *"preflight is a per-task JIT phase, not a global pre-DAG phase"* is **half right**, and
the partition records exactly which half. The preflight space has three buckets that look alike
(each is "a deterministic check run before work") but differ on the two axes that matter: **WHEN** the
claim is true (run-global vs at-the-moment-a-task-runs) and **WHOSE worktree** it is true in (the
starting repo vs the consuming task's forked `taskBase`). Place a preflight by those axes, not by
polarity alone.

### Bucket A — Shared positive baseline ("this AREA starts from green") — SETTLED, doctrine

**Claim:** the touched area's existing verification (its tests, its build) is **green at the start of
the run**. The #181 case.

**Why it has no honest per-task home — and therefore is NOT moved per-task.** A start-from-green
baseline is a property of an **AREA**, not of a task. If it were attached to a task as a per-task JIT
preflight it would have to be one of two dishonest things:

1. **"First task only" lie** — run the baseline only at the first task that touches the area, and the
   model has to encode "first" (which task? a race in parallel mode? the property is about the area,
   not about whichever task happens to be scheduled first). The baseline's truth has nothing to do
   with *which* task runs first.
2. **"Duplicate N times on post-merge bytes" error** — run it at every task that touches the area,
   and the later tasks evaluate it at *their* `taskBase`, which has **earlier siblings' merges
   already applied**. A start-from-green check at a later `taskBase` is no longer checking the
   *start* — it is checking post-merge bytes, which is a different (and union-inverting) claim.

The honest home is exactly what ships: **one no-op-action ROOT task carrying the area's baseline
guardrail, that every modifier in the area `dependsOn`** — evaluated once, against the run's starting
bytes, deduped per area. **plan-09's settled answer: Bucket A stays doctrine and is explicitly NOT a
per-task preflight.**

### Bucket B — Negative / assert-absent baseline ("the thing this task adds is absent now") — SETTLED, one-shot only

**Claim:** the artifact the plan will *introduce* (a `RequestId` field, a new route, a new
registration) is **absent now**, so a later "it's present" gate is provably the plan's own doing.

**Why it is FORBIDDEN at per-task scope.** Negative polarity is, by construction, a claim that is
**true before the run and false after the work is done** — the same shape that makes
`tests-fail-on-current-code` a `local` (never `integration`) guardrail (#165): re-run it at a *later*
task's `taskBase`, after the producing task has merged, and the thing **is** present, so the
"assert-absent" check **false-halts**. A per-task negative preflight at a downstream `taskBase`
re-imports the **union-inversion class** (the #165 / #132 lesson) exactly. Negative polarity is
inherently a **"before the whole run"** claim; it has meaning at one point in time only — the run's
start.

**Therefore Bucket B runs ONCE, pre-merge** — today as a doctrine root no-op task (the same shape as
A, inverted polarity); as the deferred global one-shot *if* a first-class assert-absent capability is
ever built. **Record explicitly: a Bucket-B check is NEVER part of the integration / union re-verify
set and NEVER part of the terminal gate.** It is a cross-reference to, not a fork of, the
`tests-fail-on-current-code` / `tests-fail-on-stubs` anti-tautology archetype: a negative baseline is
the *generalization* of that archetype to non-test artifacts (a route, a registration), evaluated
one-shot at the start, not a competing mechanism.

### Bucket C — Per-task JIT dependency-delivery precondition — the ONLY first-class candidate, DEFERRED

**Claim:** *"did my producer actually deliver the type / route / symbol I build against — in MY
forked worktree at `taskBase`, at the moment I run?"* A consuming task `dependsOn` a producer; the
producer was supposed to add `IPaymentGateway`, or wire `/health`, or export a symbol. Bucket C
verifies, **inside the consumer's own segment worktree at `taskBase`, before the consumer's action
runs**, that the producer's contribution is actually present in the bytes the consumer inherited.

**Why this is the one slice the global pre-DAG phase structurally CANNOT express.** The pre-DAG phase
(the earlier draft's shape) runs **before any producer has run** — so it can *never* check "did my
producer deliver," because at pre-DAG time no producer has produced anything. Bucket A and B are true
or false against the *starting* repo, which the pre-DAG phase can see; Bucket C is true or false
against an *intermediate, per-consumer* state that only exists once an upstream task has merged into
the consumer's `taskBase`. That is a fundamentally **per-task, just-in-time** evaluation — and it is
the *only* thing in the preflight space that is. This is the half of the pushback that is right.

**Polarity:** Bucket C is **positive and monotone-safe under merges** — "the type IS present" only
*becomes more true* as more merges land, so it never union-inverts the way a negative check does.
That monotone-safety is part of what makes it the safe per-task slice (and part of the trigger, §"The
sharpened trigger").

**Status: DEFERRED behind the sharpened trigger, AND behind the folder-vs-flag open decision.** Even
having isolated Bucket C as the one genuine first-class candidate, *how* it is realized is unsettled
(§"The open decision"), and *whether a real instance of it exists that is not reducible to a plain
`dependsOn` edge or the consumer's own first guardrail* is unproven (§"The sharpened trigger").

### The decidability heuristic (carried forward, applies to A/B authoring; C is producer-keyed)

Two predicates with sharply different decidability — conflating them is how auto-derivation goes
wrong:

- **D1 — "target pre-exists" is DECIDABLE** from the `plan-breakdown` Step-0 scan. Whether the
  artifact a Bucket-A/B baseline references *already exists in the repo* is observable at breakdown
  time. A baseline is **only** emittable when D1 holds — there is nothing to baseline (A) or to
  assert-absent against a known prior shape (B) if the thing's context does not exist.
- **D2 — "modifies-not-creates" is an AUTHORING HEURISTIC ONLY.** Whether the task *modifies* a
  pre-existing thing or *replaces / ignores* it is a judgment about intent that **neither the harness
  nor a reliable static rule can make** (the undecidability that kills auto-derivation). D2 lives in
  the **skill's** authoring judgment and **MUST UNDER-FIRE**: when in doubt, emit no preflight. A
  missing preflight costs a (rare) full-budget burn; a wrongly-emitted one false-halts a *correct*
  plan — strictly worse — so the heuristic is biased to silence.

For Bucket C the keying is different and *easier*: a C precondition is keyed to a **`dependsOn`
edge** (consumer→producer) the author already drew — "this consumer depends on that producer for
symbol X." There is no modify-vs-create judgment; there is a concrete delivered artifact named by the
dependency. **The harness still never derives it** — the skill authors the C precondition against the
edge, the human reviews it, the harness only runs what was authored (the auto-derivation rejection,
restated as a contract).

---

## The deferred Bucket-C design (the plan if/when triggered)

This is the design to implement **if** the trigger (§"The sharpened trigger") fires **and** the
folder-vs-flag open decision is resolved — not before. It is recorded now so the decision is captured
while the 4-lens context is fresh, and so a future implementer inherits a vetted starting point.

### The model — a per-task JIT precondition at `taskBase`

A Bucket-C preflight is a **deterministic check the consuming task runs in its own segment worktree
at `taskBase`, before its action, that fails fast (without burning a retry attempt) if the
dependency it builds against was not delivered.** It is *not* a guardrail in the attempt lifecycle (a
guardrail runs *after* the action and *consumes* an attempt); it is a **precondition** that runs
*before* the action and short-circuits the attempt loop entirely if the inherited bytes are not as
the dependency promised.

Two candidate realizations remain open (§"The open decision"); both share this runtime shape:

- **Where it runs:** in the consuming task's **segment worktree at `taskBase`** — the bytes the
  consumer actually inherited, *after* its producers merged in. This is the state no pre-DAG phase
  can see.
- **When it runs:** inside `TaskExecutor.ExecuteAsync`, **before the attempt loop** — it gates entry
  to the loop. A pass lets the attempts proceed normally; a fail short-circuits to `needs-human`
  **without consuming a retry attempt** (the no-burn property).
- **Read-only:** it precedes the action, so it writes no fragment and no commit (invariant 2). It is
  a single-shot deterministic byte check (see the live-probe ban below).
- **Blast radius:** a Bucket-C failure blocks **only that task and its transitive dependents** via
  the existing scheduler path; **independent branches keep running.** This is *strictly better* than
  the withdrawn global phase's plan-wide halt — it reduces the old flaky-SPOF BLOCKER (e) from
  whole-plan to **per-cone**.

### Live-probe ban (a HARD constraint on Bucket C — lifted verbatim from the volume-control gate)

A Bucket-C precondition is a **deterministic byte check only**: **single-shot, NO process start, NO
poll, NO network.** The intuitive "is my dependency's endpoint *up*?" must be expressed as a
**byte-check on the wired source** (e.g. `Select-String 'MapGet("/health")'` over the producer's
committed file in `taskBase`), **never** as `Invoke-WebRequest` / a live HTTP call. This is the same
rule the volume-control gate's "deterministic-and-cheap (NO process start)" clause states for
baselines; it is **lifted verbatim into the Bucket-C contract**, not softened. A live probe in a
precondition is exactly the flake the ban exists to forbid — and even reduced to per-cone (above), a
flaky precondition is a needless intermittent block on a whole sub-DAG.

### Harness feasibility (the harness-developer assessment — a feasibility note, still gated)

Recorded as the forward implementation shape so the deferred scope is honest. **Every item is forward
design, not yet in the SSOT or the code:**

- **Slot-in point:** Bucket C slots into `TaskExecutor.ExecuteAsync` **before the attempt loop**.
- **Runner reuse:** it reuses the existing attempt-decoupled **`IReVerifier`** seam (SSOT §4.3 — it
  already runs a guardrail set against arbitrary bytes outside an attempt lifecycle, cwd = a given
  worktree, no `GUARDRAILS_ACTION_*` vars) — here pointed at the consumer's `taskBase`. **No new
  guardrail-runner machinery.**
- **Parallelism:** naturally parallel — each consumer evaluates its own precondition in its own
  worktree; dependents block via the **existing scheduler path** (the same closure used for
  `needs-human` propagation).
- **It DISSOLVES the #182 / serial-mode prerequisite** the withdrawn global design treated as a
  blocker: a Bucket-C precondition **short-circuits the loop and never enters the retry budget**, in
  **both** modes — so the "cheap halt depends on the serial-mode no-op short-circuit" coupling
  (BLOCKER (c)) does not apply to C. (It still applied to the *doctrine* Bucket-A red-halt, which is
  why #182 shipped — but C's fail-fast is structural, not budget-dependent.)
- **Outcome:** `precondition-failed` → `needs-human` → **exit 2** (no new exit code — exit 2 already
  means "actionable condition found; work durable / unstarted").
- **Effort: S–M.**

These are the **forward implementation shape**, still gated on the trigger and the open decision.

> **Worked example.** A SIMULATED breakdown lives at
> [`09-preflight-first-class/example/`](09-preflight-first-class/example/). It currently illustrates
> the *earlier* (global pre-DAG `scope:"precondition"`) shape and **will be re-authored to the
> partition in a separate reviewed step** — recasting its three "preflight" tasks as a Bucket-A
> doctrine root, a Bucket-B one-shot, and a single Bucket-C consuming-task illustration (folder or
> flag), with the simulated third scope value removed. Until then, read the example as *historical*
> against this revised doc; the re-author spec is in the rewrite report accompanying this change.

**Explicitly recorded: AUTO-DERIVATION IS REJECTED.** No part of a Bucket-C implementation may have
the harness infer pre-applicability. The harness runs an *authored* precondition; it never decides
*which* checks should be preconditions. The skill authors the C precondition against a `dependsOn`
edge, the human reviews it.

---

## The open decision — `<task>/preflights/` folder vs `task.json` no-burn flag (DO NOT resolve here)

**How Bucket C is realized is unsettled.** This is the load-bearing open decision; this document
frames it and does **not** resolve it. Two options, then the decisive question.

### Option 1 — a new optional `<task>/preflights/` folder

A consuming task gains an optional sibling of `guardrails/` named `preflights/`, holding ≥1
deterministic check run at `taskBase` before the action. Strengths (the architect's earlier
preference):

- **Self-documenting** — `preflights/` *names* the precondition concept on disk; a reviewer sees it
  is not an ordinary guardrail.
- **Physically separate from lifecycle guardrails** — it is structurally *impossible* for a
  precondition to drift into the attempt lifecycle, the integration set, or the terminal gate, because
  it lives in a different folder the runner treats differently. (This directly answers the
  union-inversion concern by construction — see BLOCKER (d) disposition.)

Costs: a **new folder** in the task contract; a **new `TaskOutcome.precondition-failed`**; a **new
GR2027** (next free structural code — live highest is GR2026 — for a malformed `preflights/` entry,
e.g. a non-deterministic precondition or a precondition with no `dependsOn` edge to key against); and
a **validator surface** to parse/validate it.

### Option 2 — a `task.json` flag on the existing FIRST guardrail

The devil's-advocate **F5 reduction**: if the *only* behavioral delta over "the consuming task's own
first guardrail" is **fail-fast without burning a retry attempt**, then a **one-line `task.json`
flag** — marking the task's existing first guardrail as a no-retry-burn precondition — **beats a whole
new folder.** No new folder, no new outcome string beyond a flag's branch, far less validator surface.
The check still lives in `guardrails/01-…`; the flag changes only *when* it runs (before the action,
gating loop entry) and *what a failure costs* (no attempt burned).

### The decisive question (answer BEFORE any build)

**Is a Bucket-C dependency-delivery check genuinely DISTINCT from "the consuming task's first
guardrail + a no-burn flag"?**

- **If the only delta is no-burn fail-fast → Option 2 wins.** A precondition that is just "run the
  consumer's first check early and don't spend an attempt if it fails" is a *scheduling* tweak, and a
  flag is the KISS expression of a scheduling tweak. A new folder would be ceremony (the
  no-machinery-for-no-new-behavior rule again).
- **If it needs true precondition semantics the guardrail lifecycle cannot express → Option 1 wins.**
  If a C check must run *before* the action with a *distinct outcome* and a *distinct on-disk
  identity* that a lifecycle guardrail structurally cannot carry (e.g. it must never be eligible for
  the integration set, never be retried, never be confused with a postcondition), then the folder
  earns its surface.

**This is the load-bearing open decision. It is NOT resolved here.** It is resolved at trigger time,
informed by the *real captured instance* the trigger requires — because the instance's shape is what
tells us whether the delta is "just no-burn" (Option 2) or "true precondition semantics" (Option 1).
Deciding it now, before an instance exists, risks building the wrong one.

---

## The BLOCKERs and their dispositions under the partition

From the adversarial lens. The withdrawn global pre-DAG phase had to clear all six as live gates; the
**partition dissolves or reduces most of them**, because Bucket C is a per-task JIT check on the live
path, not an up-front whole-plan phase. Each is recorded with its disposition.

- **(a) Cost-on-the-common-green-path / EV-inversion ("many preflights") — REDUCED.** The global phase
  ran N checks unconditionally up front on every (usually green) run — an EV-inversion as preflights
  multiply. **Bucket C runs only along the LIVE path:** a consumer's precondition runs only when that
  consumer runs, gated by its own `dependsOn` arrival — there is **no unconditional up-front cost**.
  Bucket A stays **deduped one-per-area** (the volume-control gate). The EV-inversion of "N preflights
  every run" does not arise for C, and is bounded for A by dedup. Still bounded by the volume-control
  gate below.

- **(b) The undecidable modify-vs-create judgment → false-halt + vacuous-green — UNCHANGED for A/B,
  N/A for C.** D2 (modifies-not-creates) stays **out of the harness entirely** (authoring-only,
  under-firing) for Bucket-A/B baselines. **Bucket C does not face D2 at all** — it is keyed to a
  concrete `dependsOn` edge naming a delivered artifact, not to a modify-vs-create intent judgment. A
  false-halt (a correct plan refused) remains the worst failure mode for A/B authoring; the under-fire
  rule and `guardrails-review` are the mitigation.

- **(c) Serial-mode fast-halt (depends on #174 / #182) — DISSOLVED for C.** The withdrawn design
  coupled the cheap halt to the #174/#182 no-op short-circuit (worktree-only until #182). **Bucket C
  does not need that coupling:** a precondition **short-circuits the attempt loop and never enters the
  retry budget**, in **both** serial and worktree mode (harness feasibility note). #182 still shipped
  for the *doctrine* Bucket-A red-halt (a real Phase-1 need), but C's fail-fast is **structural**, not
  budget-dependent — so this is no longer a Bucket-C blocker.

- **(d) Negative-baseline union-inversion — DISSOLVED for C; it is the REASON B is one-shot-only.**
  A negative check is red after the work is done — re-running it post-merge false-halts (#165/#132).
  Disposition is two-sided: **(i) Bucket C never joins the integration / union / terminal set** (it is
  a per-task precondition at `taskBase`, never re-run on merged bytes — by construction under either
  realization: a `preflights/` folder the runner never adds to the integration set, or a flagged
  guardrail that gates loop-entry only). **(ii) This same inversion is precisely WHY Bucket B negative
  polarity is FORBIDDEN per-task** and stays a one-shot at the run's start — a negative check at a
  downstream `taskBase` sees earlier merges and false-halts. Union-inversion is dissolved for C and is
  the load-bearing argument for B's one-shot placement.

- **(e) A flaky preflight = a single point of failure — REDUCED from plan-wide to per-cone.** The
  global phase halted the *entire run* on a flake (maximal blast radius). **A Bucket-C failure blocks
  only that task + its transitive dependents; independent branches keep running** (harness feasibility
  note). Combined with the **live-probe ban** (deterministic byte checks only — NO process start, poll,
  or network), a C precondition cannot be a network-flake SPOF at all, and even a deterministic
  failure is scoped to one cone. **Strictly better than the global phase's plan-wide halt.**

- **(f) Naming collision (`scope: "precondition"` vs SSOT §2 "pre-flight") — DISSOLVED.** The withdrawn
  design proposed a *third* guardrail-`scope` value `"precondition"`, colliding with the run-level
  "pre-flight" gate (GR2015) in SSOT §2. **Bucket C uses neither a third scope value nor the word
  "preflight" as a scope:** it is a `<task>/preflights/` **folder** (Option 1) or a `task.json`
  **flag** (Option 2). No third `scope` value is added. The collision does not arise. (The doctrine
  archetype keeps using **"baseline"** in the catalogue — never "preflight" — preserving "pre-flight"
  for the run-level §2 gate, exactly as today.)

---

## Volume-control gate (the "worth-it" rule — re-pointed to the partition)

The volume-control gate is the skill-author's discipline that keeps preflights from inverting the
cost model and from becoming a flaky SPOF. **Under the partition it splits cleanly by destination:**

**For a SHARED baseline (Bucket A — a doctrine no-op ROOT task), emit ONLY when ALL hold:**

1. **Pre-exists** — the baselined thing already exists in the repo (D1, decidable from the Step-0
   scan). Nothing to baseline otherwise.
2. **Modifies** — the task *modifies* it, not *creates / replaces* it (D2, the authoring heuristic —
   **under-fire** when in doubt).
3. **Deterministic-and-cheap — NO process start.** A fast deterministic evaluation (a file grep, a
   static analysis, an already-fast unit subset) — **never** a server start, a network endpoint hit,
   or a flaky timing-dependent probe. (This same clause is **lifted verbatim into the Bucket-C
   live-probe ban**.)
4. **Strictly-narrower-than-the-terminal-gate.** The baseline verifies *less* than the run's terminal
   whole-repo gate (it baselines the *touched area*, not the whole repo) — never a duplicate of the
   terminal suite.
5. **Shared-by-≥2-tasks.** The baseline is a precondition for **two or more** modifier tasks. A
   baseline relevant to exactly one task is just that task's own first guardrail — no baseline needed.
6. **Deduped-per-area.** One baseline per touched area, not one per task — N tasks touching the same
   area `dependsOn` ONE no-op ROOT task.

**For a PER-TASK dependency-delivery precondition (Bucket C — DEFERRED), additionally:** the
**live-probe ban** (clause 3 verbatim — deterministic byte check, NO process/poll/network); the check
is **positive / monotone-safe under merges** (not negative — negatives are Bucket B, one-shot); and it
is **not reducible** to a plain `dependsOn` edge or the consumer's own first guardrail (else it is not
a distinct preflight — the open decision's decisive question). Bucket C is *not* subject to the
"shared-by-≥2 / deduped-per-area" clauses — it is intrinsically per-consumer.

If a candidate fails **any** applicable clause, it is **not emitted** — it stays an ordinary task
guardrail (or is omitted). The gate is biased to *not* emit, consistent with the under-fire rule.

---

## The sharpened trigger for Bucket C (the gate to actually build it)

The earlier trigger ("a dogfooded *non-test* preflight + cost evidence that *many* preflights pay")
was aimed at the withdrawn global phase. The partition **replaces it** with a trigger aimed precisely
at Bucket C. Build Bucket C **ONLY** when a DOGFOODED case appears that is **ALL** of:

- **(a) Task-local** — about a value an *earlier task in the run* produced into the consumer's
  `taskBase`, **not** run-global like start-from-green (which is Bucket A doctrine).
- **(b) Positive-polarity / monotone-safe under merges** — "the type / route / symbol IS present,"
  which only becomes *more* true as merges land — **not** negative / assert-absent (which is Bucket B,
  forbidden per-task, one-shot at the start).
- **(c) NOT reducible** to the consuming task's own first guardrail **or** to a plain `dependsOn`
  edge. If a `dependsOn` edge already guarantees the producer ran and the consumer's first guardrail
  already catches the missing symbol at normal attempt cost, there is nothing distinct to build — this
  is the open decision's decisive question, applied as a trigger clause.
- **(d) NOT already caught by the #174/#182 no-op short-circuit** — if the consumer's first attempt
  already fails-and-escalates cheaply via the existing no-op short-circuit, the marginal value of a
  precondition is nil.

**Candidate archetypes (not yet captured):** the **#176 transitive-compile-dependency** shape (a
consumer that compiles against a type a non-adjacent producer was supposed to add) and the **#159
stale-union** shape are the most likely places a real Bucket-C instance will surface. **But a REAL
captured instance is required — not a hypothesized one.** A worked sketch is not a trigger; a dogfood
run that *actually* produced the shape, with the four clauses demonstrably satisfied, is.

**Absent such an artifact, Bucket C stays deferred and the per-task-JIT pushback is recorded as
"considered; scoped to dependency-delivery; gated."** Buckets A (doctrine, shipped) and B (doctrine
one-shot) + the #182 fix are the delivered answer to #183. This issue (#183) remains the durable home
for the trigger watch.

---

## Contract / SSOT impact

**THIS REWRITE MAKES NO SSOT CHANGE.** Bucket C stays deferred; per invariant 4 a contract lands in
`02-schemas-and-contracts.md` only in the change that implements it. This section is a **forward
design** so the future change is pre-scoped — nothing here is applied now.

**Buckets A + B (doctrine): NO contract change.** Expressible with the existing `task.json`,
guardrail, and `dependsOn` primitives — the proof that they are doctrine, not features (invariant 4).
The change lives entirely in the `plan-breakdown` catalogue and `guardrails-review` (skill files).

**#182 (serial-mode no-op short-circuit): a behavior fix, no new contract.** The #174 no-op
short-circuit (SSOT §7) was extended to fire in serial mode; the §7 narrative gained a sentence on
serial-mode coverage. No new field, status, or code.

**Bucket C (DEFERRED): the forward SSOT edits — NOT YET APPLIED, and SHAPE-DEPENDENT on the open
decision.** The edit set differs by realization; both are recorded so whichever wins is pre-scoped:

- **If Option 1 (`<task>/preflights/` folder):**
  - **§3 / §3.x (task.json / task folder layout)** — define the optional `<task>/preflights/` sibling
    of `guardrails/`: ≥1 deterministic check, run at `taskBase` before the action, keyed to a
    `dependsOn` edge. A new validation rule (preconditions are deterministic-only — live-probe ban;
    keyed to an existing `dependsOn` edge).
  - **§7 (run.json / outcomes)** — a new `outcome`: **`precondition-failed`** → `needs-human` (the
    consuming task did not start its action). Distinct from a postcondition `needs-human`.
  - **§7.1 (exit codes)** — extend the **exit 2** narrative to name a precondition halt as one of its
    causes (**no new code** — exit 2 already means "actionable condition found; work durable /
    unstarted").
  - **Diagnostic codes** — **GR2027** (next free structural code; live highest is GR2026) for a
    malformed `preflights/` entry.
- **If Option 2 (`task.json` no-burn-precondition flag):**
  - **§3 / §3.x (task.json)** — a single optional flag marking the task's existing FIRST guardrail as
    a no-retry-burn precondition (run before the action, gating loop entry; a failure does not consume
    an attempt). Far smaller surface — **no new folder, no new scope value.**
  - **§7 / §7.1** — the same `precondition-failed` / exit-2 disposition; possibly no GR2027 if the
    flag's validation folds into existing guardrail validation.
- **In NEITHER option** is a third `scope` value (`"precondition"`) added — the partition dissolved
  that (BLOCKER (f)). **Naming** stays: "**baseline**" for the Bucket-A/B authoring archetype,
  "**precondition**" for the Bucket-C concept, and "**pre-flight**" reserved for the run-level §2 gate;
  the word "preflight" as a *scope value* is not introduced.

---

## Open decisions (resolve BEFORE Bucket C is built)

1. **THE LOAD-BEARING ONE — `<task>/preflights/` folder (Option 1) vs `task.json` no-burn flag
   (Option 2)** (§"The open decision"). Decided by the decisive question: is a Bucket-C check
   genuinely distinct from "the consumer's first guardrail + a no-burn flag"? Resolved at trigger
   time, **informed by the real captured instance** the trigger requires — *not* pre-decided here.
2. **`precondition-failed` outcome + exit-2 mapping.** Confirm the new `outcome` string
   (`precondition-failed`) and that it maps to **exit 2** (no new code). Common to both options.
3. **GR2027 scope.** Whether a malformed precondition needs a dedicated **GR2027** (Option 1, a
   `preflights/` entry) or folds into existing guardrail validation (Option 2, a flag).
4. **Precondition read-surface.** Whether a Bucket-C precondition may read `GUARDRAILS_STATE_IN`
   (it runs after producers, so producer state *does* exist at `taskBase` — unlike the withdrawn
   pre-DAG phase) — and the live-probe ban (deterministic byte check only) as a hard validation rule.
5. **Naming confirmation.** "**baseline**" for the Bucket-A/B archetype, "**precondition**" for the
   Bucket-C concept, "**pre-flight**" reserved for the run-level §2 gate; no new `scope` value, no
   "preflight" scope token. (Recommended; touches SSOT vocabulary, needs product-owner sign-off.)

---

## Devil's-advocate self-critique

Run against my own partition, per the operating contract. The strongest counter-arguments and my
responses:

- **Counter (strongest, against the partition itself): "Three buckets is over-taxonomy. You have
  manufactured a distinction (A vs B vs C) to *look* like you resolved the per-task-vs-global pushback,
  when in truth all three are 'a deterministic check run before some work' and a single mechanism could
  carry all three — the partition is complexity dressed as rigor."** *Response:* The buckets are not a
  presentation device — they differ on a **load-bearing operational axis** that a single mechanism
  provably cannot span: **where and when the claim is true.** A (start-from-green) is true only against
  the run's *starting* bytes; B (assert-absent) is true only at the run's *start* and false ever after
  (it union-inverts if re-run); C (dependency-delivered) is true only in a *consumer's* worktree
  *after a producer merged in*. A single "global pre-DAG phase" can express A and B but is *structurally
  blind* to C (no producer has run). A single "per-task JIT check" can express C but **lies or
  duplicates** for A (no honest per-task home) and **false-halts** for B (union-inversion at a
  downstream `taskBase`). No one mechanism spans all three without one of these failures — that
  impossibility *is* the partition. The taxonomy earns its keep by telling us A/B are doctrine (no
  build) and only C is a first-class candidate (and even C is gated). It *reduces* total built
  machinery, it does not add it.

- **Counter: "The live-probe ban rules out the most compelling preflight — 'is my dependency's
  endpoint *up*?' — which is the exact 'plane on the runway' intuition #183 opens with."** *Response:*
  Correct, and it is an honest tension — but the partition handles it better than the withdrawn global
  phase did. A live endpoint probe is *exactly* the flaky SPOF the ban forbids. Under the partition the
  ban costs less: the "endpoint up" intuition becomes a **byte-check on the wired source** in the
  consumer's `taskBase` (the route is `MapGet`-registered in the producer's committed file) — which is
  deterministic, single-shot, and *is* expressible as Bucket C. A genuinely *live* check still belongs
  in a task's own guardrail (flake blast-radius = one task's retry budget), exactly as
  `05-wire-…`'s `health-still-200` does in the example. The ban is a conservative default lifted
  verbatim from the volume-control gate, not a new veto.

- **Counter: "Reserving Bucket C as a per-task JIT slice is just the pushback re-badged — you conceded
  the per-task model and dressed the concession as a partition."** *Response:* Partly conceded, and
  that is the honest record: the pushback's *half that is right* (a genuine per-task JIT need exists) is
  adopted as Bucket C. The *half that is wrong* (that ALL preflight is per-task JIT, including
  start-from-green) is rejected with a concrete failure for each: A has no honest per-task home, B
  false-halts per-task. So this is not a re-badge — it is an adjudication that took one slice and
  refused the other two, with stated reasons. And even the adopted slice is **gated** behind a real
  captured instance and the folder-vs-flag decision, so nothing is conceded into a build prematurely.

- **Counter: "The folder-vs-flag decision is being dodged — an architect who cannot decide between a
  new folder and a one-line flag has not finished the design."** *Response:* It is deliberately, not
  lazily, left open. The decisive question — is a C check distinct from "the consumer's first guardrail
  + a no-burn flag"? — is **answerable only by the shape of a real instance**, which the trigger
  requires before any build. Deciding now, instance-free, is precisely how one builds the *wrong*
  surface (a whole `preflights/` folder for what turns out to be a one-line flag, or a flag too weak
  for what turns out to need true precondition semantics). The KISS-correct move is to record both
  options, the question that chooses between them, and the artifact that answers the question — then
  stop. That is finished design discipline, not an unfinished design.

---

## Implementation handoff (agent + filesTouched + sequencing)

**This plan authorizes NO Bucket-C implementation.** The handoff below is the **trigger-time** plan —
the sequence to execute *if and only if* the §"The sharpened trigger" criteria fire. Buckets A + B
ship *now* as doctrine under #181 (+ the #182 fix), not under this document.

**Now (doctrine, Buckets A + B — under #181 / #182, for reference, not this plan's deliverable):**
- `guardrails-skill-author` — `plan-breakdown` catalogue (the "baseline" archetype: positive (A) +
  negative (B) polarity, the volume-control gate, the under-fire D2 rule, cross-reference to
  `tests-fail-on-current-code`, and the explicit "A is NOT per-task / B is one-shot-only" rules) +
  `guardrails-review` probe. `filesTouched`: `.claude/skills/plan-breakdown/**`,
  `.claude/skills/guardrails-review/**`. (#181)
- `guardrails-harness-developer` — the #182 serial-mode no-op short-circuit fix + tests.
  `filesTouched`: `src/Guardrails.Core/Execution/**` (the short-circuit), `tests/**`. (#182, shipped)

**Trigger-time (Bucket C — gated, do NOT start until the trigger fires AND a real instance is
captured):**
1. **Architect (this agent)** — re-open this plan, **resolve the folder-vs-flag open decision against
   the captured instance**, resolve the remaining §"Open decisions", deliver the Bucket-C design as a
   **draft PR for inline human review** (#106 design-of-record → draft-PR loop) **before** any
   implementation milestone. `filesTouched`: `docs/plans/09-preflight-first-class.md` (promote from
   DEFERRED to active) + the verbatim `docs/plans/02-schemas-and-contracts.md` edit set (whichever
   option won).
2. **`guardrails-harness-developer`** — the chosen Bucket-C realization: **Option 1** =
   `<task>/preflights/` folder parsing/validation (GR2027) + the `TaskExecutor.ExecuteAsync`
   pre-attempt-loop precondition check (reusing `IReVerifier` at `taskBase`) + `precondition-failed`
   outcome + exit-2 branch; **Option 2** = the `task.json` no-burn-precondition flag + the same
   pre-loop gating + outcome. `filesTouched`: `src/Guardrails.Core/Loading/**`,
   `src/Guardrails.Core/Execution/**` (the `TaskExecutor` pre-loop slot), `src/Guardrails.Core/Model/**`,
   `src/Guardrails.Cli/**`, `docs/plans/02-schemas-and-contracts.md` (same change), `tests/**`.
   Sequencing: SSOT edit + parsing/validation first, then the pre-loop check + outcome + exit code.
3. **`guardrails-skill-author`** — teach the catalogue to emit the Bucket-C precondition (folder or
   flag) for the qualifying dependency-delivery case, keyed to the `dependsOn` edge, keeping Bucket-A/B
   doctrine unchanged. `filesTouched`: `.claude/skills/plan-breakdown/**`,
   `.claude/skills/guardrails-review/**`.
4. **`guardrails-test-author`** — Bucket-C pre-loop tests (pass → loop proceeds; fail → `needs-human`
   with **no attempt burned**, in BOTH modes), the **never-joins-the-integration-set** assertion
   (BLOCKER (d)), the per-cone blast-radius test (independent branches keep running), and the
   live-probe-ban rejection test. `filesTouched`: `tests/**`.

Sequencing rule: the architect's draft-PR review (step 1) — including the **folder-vs-flag
resolution** — must complete before any harness work (step 2) starts (#106). #182 is already shipped,
so it is no longer a Bucket-C prerequisite (C's fail-fast is structural, not budget-dependent —
BLOCKER (c) disposition).

---

## Proposed plan-document edits

This document is itself the new plan-of-record (`docs/plans/09-preflight-first-class.md`). The
following companion edits are **proposed, not yet applied** (the lead approves, then they are made):

1. **`docs/plans/03-roadmap.md`** — no v1-milestone change. Optionally add a one-line "deferred
   designs" pointer: *"Preflight first-class — DEFERRED design of record in
   `09-preflight-first-class.md` (#183). The preflight space is partitioned: Buckets A
   (start-from-green) + B (assert-absent) ship as doctrine (#181, +#182 fast-halt); only Bucket C
   (per-task JIT dependency-delivery) is a first-class candidate, gated on a real captured instance and
   an unresolved `preflights/`-folder-vs-`task.json`-flag decision."* (Proposed; the roadmap's v2-bet
   list is for active bets, and Bucket C is deferred-behind-a-trigger, so a pointer is lighter than a
   full bet entry.)
2. **`docs/plans/02-schemas-and-contracts.md`** — **NO edit now.** The shape-dependent forward edit
   set in §"Contract / SSOT impact" lands here **only** in the change that implements Bucket C, with
   whichever realization the open decision selects (invariant 4). Recorded here so the future change is
   pre-scoped.
3. **`docs/plans/README.md`** (the plan index) — add the `09-preflight-first-class.md` entry as a
   **DEFERRED design**, distinguishing it from the active plan-of-record docs. (Proposed.)
4. **`docs/plans/09-preflight-first-class/example/`** — the worked example will be **re-authored to the
   partition** in a separate reviewed step (NOT this pass): the three current "preflight" tasks are
   re-cast as **Bucket A doctrine roots** (`00-baseline-core-tests-green`), **Bucket B one-shot**
   (`02-baseline-correlation-absent`), and a **single Bucket-C consuming-task illustration** carrying a
   `preflights/`-folder (or flagged-guardrail) dependency-delivery check; the simulated
   `scope:"precondition"` markers are removed entirely (no third scope value exists under the
   partition). See the rewrite report for the precise re-author spec. (Pointer added; the example
   itself is unchanged by this pass.)

No skill or SSOT edit is made by this document — Buckets A + B ship under #181 (+#182); this plan
records the deferred Bucket-C design, its sharpened trigger, and the folder-vs-flag open decision.
