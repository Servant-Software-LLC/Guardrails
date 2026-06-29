# Architecture: Preflight as a first-class citizen — DEFERRED design (#183)

> **Status: DEFERRED design-of-record.** This document is NOT an input to `/plan-breakdown` and
> authorizes NO implementation milestones today. It records (1) the generalized **preflight
> principle**, (2) the **determination** — reached by a 4-lens review (architect /
> devil's-advocate / harness-developer / skill-author) that **unanimously converged** — to ship
> the principle as **DOCTRINE first** and **defer first-class status**, (3) the **deferred
> Phase-2 first-class design** so it is ready if and when the trigger fires, and (4) the explicit
> **trigger criteria** and **BLOCKERs** any Phase-2 work must clear before it may start. The
> contract additions sketched in §"Contract / SSOT impact" are a **forward design**, not yet
> applied to `02-schemas-and-contracts.md` (the SSOT) — they land there only in the change that
> implements Phase 2, never before (invariant 4).
>
> **One-line claim, scoped honestly:** a brownfield task that **modifies** (not creates) a verified
> thing can have its end-of-task gate evaluated *before* the task — a **preflight baseline**. The
> principle is real and ships now as doctrine; it is **fully expressible with existing primitives**
> (a no-op-action ROOT task + a guardrail + a DAG edge), so a schema field / harness phase / new
> exit code would be **machinery for no new behavior** — ceremony — until a real dogfooded
> *non-test* preflight proves the doctrine archetype cannot express it. "A prompt may propose, only
> a deterministic gate may certify" — and a preflight is just that gate, run one step earlier to
> fail fast and attribute correctly.

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
**preflight** establishing a baseline. Two polarities:

- **POSITIVE baseline** ("already green / already up") → **fail fast on a broken start.** The thing
  the task will modify is verified to be working *now*; if it is not, halt before spending the task.
  Beyond unit tests: an endpoint already responding, a build already green, a DI registration
  already present, a schema already valid, a route already resolving.
- **NEGATIVE baseline** ("not yet present") → **attribution.** The thing the task will *add* is
  verified to be **absent** now, so a later "it's present" gate is provably the task's own doing,
  not pre-existing. **This polarity already exists** as the `plan-breakdown`
  `tests-fail-on-current-code` / `tests-fail-on-stubs` anti-tautology archetype — a preflight design
  must **cross-reference** it, never fork it (see §"Negative vs positive modeling").

**Ambiguity named & narrowed.** The word "first-class" is the load-bearing ambiguity, and the
brief leaves three readings open. I narrow them up front so the determination below is unambiguous:

1. **"First-class" = a recognized authoring archetype** (doctrine: a named pattern
   `plan-breakdown` emits and `guardrails-review` probes for). **This is Phase 0 — shipping now.**
2. **"First-class" = a harness-acted contract** (a `task.json` field, a dedicated pre-DAG phase, a
   distinct journal status / outcome / exit-code branch). **This is Phase 2 — DEFERRED**, designed
   below, gated on the trigger.
3. **"First-class" = the harness auto-derives preflights** (it inspects a task and decides "this
   modifies, so inject a baseline"). **REJECTED outright** — "modifies-not-creates" is **undecidable
   by the harness** (see the determination and BLOCKER (b)). Auto-derivation false-fails on every
   legitimate TDD-red / coverage guardrail, which is *designed* to be red before the task.

The brief settles which reading we pursue and when: **(1) now, (2) deferred behind a trigger, (3)
never.**

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

| Concern | Phase | Placement |
|---|---|---|
| The **preflight principle** as a named authoring archetype (catalogue entry + insertion rule + a `guardrails-review` probe) | **Phase 0 (now)** | **skill** — `plan-breakdown` guardrail catalogue + `guardrails-review`; the #181 positive unit-test baseline is the first instance |
| **#181** — positive existing-tests-green baseline for brownfield plans (the canonical doctrine instance) | **Phase 0 (now)** | **skill** — `plan-breakdown` Step-0 scan + a baseline ROOT task; no harness change |
| **#182** — serial-mode fix to the #174 no-op short-circuit, so the fast red-halt holds in BOTH modes | **Phase 1 (now)** | **harness** — `TaskExecutor` / no-op short-circuit (a bug fix to an existing mechanism, NOT new machinery) |
| First-class harness contract: a guardrail `scope: "precondition"` value + a pre-DAG **preflight phase** reusing the integration worktree at user HEAD | **Phase 2 (DEFERRED)** | **harness + schema** — designed in §"The deferred Phase-2 first-class design"; lands in the SSOT only when implemented |
| The harness **auto-deriving** pre-applicability ("this task modifies, inject a baseline") | **REJECTED** | **out of scope, permanently** — undecidable; false-fails every TDD-red gate |
| Negative baseline as a NEW mechanism | **REJECTED** | **out of scope** — it already IS `tests-fail-on-current-code` / `tests-fail-on-stubs`; cross-reference, never fork |
| "Many preflights" volume / cost management | **Phase 2 constraint** | **skill** — the volume-control "worth-it" gate (§"Volume-control gate"), carried into any Phase-2 design |

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
   *Untouched by doctrine; lightly strained by Phase 2.* A doctrine preflight is a no-op-action ROOT
   task — it writes no fragment, merges nothing, and reads the user's HEAD read-only. The Phase-2
   pre-DAG phase runs **before** any segment worktree exists, against the integration worktree at
   user HEAD; it must remain a **read-only baseline check that produces no fragment and no commit**,
   or it strains this invariant. Designed to write nothing (§Phase-2).
3. **Verdicts come from files, never CLI exit codes.** *Respected.* A preflight's pass/fail is a
   guardrail verdict (deterministic exit / prompt verdict file) exactly as any guardrail — the
   preflight is not a new verdict source, just the same source evaluated one step earlier.
4. **`02-schemas-and-contracts.md` is the schema SSOT — a contract change lands there in the SAME
   change that motivates it.** *This is why Phase 2 is deferred and its SSOT edits are NOT yet
   applied.* Doctrine (Phase 0) touches **no contract** — it is fully expressible with the existing
   `task.json` + guardrail + `dependsOn` primitives, so there is no SSOT edit to make. A Phase-2
   `scope: "precondition"` value, journal status, and exit-code branch WOULD be SSOT edits — and per
   this invariant they land **only** in the change that implements them, never speculatively.
   Shipping the marker now, ahead of behavior, would put a contract in the SSOT the harness does not
   honor — the precise anti-pattern this invariant forbids.
5. **Honest halts — nothing marked done unverified; needs-human is a feature.** *Respected and
   extended.* A red positive-baseline preflight is the most honest halt there is: "your starting
   point is already broken; I will not pretend my task can fix what it does not own." The fast
   red-halt (Phase 1 / #182) makes that halt *cheap* in both modes rather than a full-budget burn.
6. **Plain files, light setup — no databases, daemons, or SaaS in v1.** *Respected.* Doctrine is
   pure authoring (files in the task folder). Phase 2 adds no dependency — it reuses the existing
   integration worktree and guardrail-runner seams.

**The decisive invariant pairing is 1 + 4.** The principle is fully expressible with existing
primitives (invariant-4 says "no SSOT edit needed → no contract change → it is doctrine"), and the
only "first-class" reading that would add real *capability* is auto-derivation — which invariant 1
forbids because "modifies-not-creates" is a non-deterministic harness judgment. What is left for a
first-class field to buy is **polish, not capability** — and a marker the harness does not act on is
**ceremony** (the no-new-machinery-for-no-new-behavior rule). Hence: doctrine first, defer the rest.

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
   behavior). A `task.json` `"preflight": true` field — or a `scope: "precondition"` guardrail value
   — that the harness merely *records* but does not *schedule differently* buys nothing the
   no-op-ROOT-task already buys. It is a label, and a label is not a feature. A field earns its place
   **only** when the harness does something with it the DAG cannot already express (a pre-DAG phase, a
   distinct outcome) — and that "something" is **polish** (one node instead of N edges; a dedicated
   journal status; a cheaper one-shot phase), not new capability.

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

**Therefore:** ship the principle as **doctrine** (Phase 0), fix the fast-halt in serial mode
(Phase 1), and **defer first-class** (Phase 2) behind an explicit trigger. First-class status buys
*polish* (a pre-DAG phase, one node instead of N, a dedicated outcome) — real, but not worth a
contract change until a dogfooded **non-test** preflight proves the doctrine archetype cannot
express it AND cost evidence shows "many preflights" pays.

---

## Phasing

| Phase | What | Where | Status |
|---|---|---|---|
| **Phase 0** | **The DOCTRINE archetype** — a named "baseline / preflight" entry in the `plan-breakdown` guardrail catalogue (positive + negative polarity, the insertion rule, the volume-control gate) + a `guardrails-review` probe; **#181** (positive existing-tests-green baseline) is the canonical first instance. **No schema field, no harness phase, no contract change.** | **skill** (`plan-breakdown`, `guardrails-review`) | **Shipping (targeted preview.34)** |
| **Phase 1** | **#182** — the serial-mode fix to the #174 no-op short-circuit, so the cheap red-halt ("the gate failed, the action changed nothing, escalate immediately rather than burn the budget") holds in **both** serial and worktree mode. A doctrine preflight's value depends on a *fast* halt; today the short-circuit is worktree-only (it needs `taskBase` to prove "no writes"). | **harness** (`TaskExecutor` / no-op short-circuit) | **Shipping (targeted preview.34)** |
| **Phase 2** | **First-class status** — the deferred design below (a `scope: "precondition"` guardrail value + a pre-DAG preflight phase). **Only if the trigger fires.** | **harness + schema + skill** | **DEFERRED** (this plan) |

**Phase 0 and Phase 1 are the product of #183 today.** Phase 2 is the contents of this plan, held in
reserve. The phasing is deliberately ordered so the cheap, contract-free, fully-expressible wins ship
first and the expensive contract change waits for evidence.

---

## The deferred Phase-2 first-class design (the plan if/when triggered)

This is the design to implement **if** the trigger (§"Trigger criteria") fires — not before. It is
recorded now so the decision is captured while the 4-lens context is fresh, and so a future
implementer inherits a vetted starting point rather than re-deriving it under pressure.

### The model (the architect-recommended minimal option)

A first-class preflight is **a guardrail with `scope: "precondition"`** run in a **pre-DAG preflight
phase**, against the **integration worktree at the user's HEAD**, before any segment worktree is
created and before the scheduler dequeues the first task.

- **`scope: "precondition"` — a third guardrail-scope value** (alongside `"local"` and
  `"integration"`, SSOT §4.3). A precondition guardrail is **not** part of an attempt lifecycle and
  **not** part of the integration set re-run at unions. It is collected at load time into the run's
  **precondition set** and run once, up front, by the preflight phase. (It attaches to the baseline
  ROOT task's `guardrails/`, so authoring stays familiar; the scope value is what lifts it into the
  pre-DAG phase instead of a normal first-wave task.)
- **The preflight phase** runs after the run-level pre-flight gate (git-repo / dirty-tree, SSOT §2 —
  note the **naming collision**, BLOCKER (f)) and **before** plan-branch / segment-worktree
  creation. It evaluates the precondition set against the integration worktree checked out at the
  **user's HEAD** (the architect-recommended reuse: no new worktree — the integration worktree
  already exists at user HEAD at this point in the run, before any task has advanced it). It is
  **read-only**: no fragment, no commit, no state write (invariant 2).
- **Outcome on a failed precondition:** the run halts **before any task starts** with a dedicated
  outcome (a NEGATIVE polarity preflight inverts: it must FAIL when the thing is unexpectedly
  *present*). No segment worktree was ever created; no token was spent on an action; the halt is the
  cheapest possible. This is the polish the field buys over N `dependsOn` edges: **one phase, one
  node, one shot** — not a baseline task replicated as a dependency of every modifier, each
  re-running the same check.

**Why reuse the integration worktree at user HEAD (vs a fresh worktree or the user's checkout):** it
is the **minimal** option — the integration worktree is created at run start off the user's HEAD
anyway (SSOT §1), so the preflight phase runs against bytes that already exist with **zero extra
worktree cost** and **zero write to the user's checkout** (read-only, invariant 2). A fresh
throwaway worktree would be pure cost; running against the user's live checkout would violate the
read-only-for-the-whole-run guarantee.

### Positive vs negative modeling

- **POSITIVE** (`tests-already-green`, `endpoint-already-up`, `build-already-green`,
  `registration-already-present`): the precondition guardrail **passes when the thing verifies** and
  **fails when it is broken**. A failure halts the run as "broken start."
- **NEGATIVE** (`not-yet-present` attribution): the precondition guardrail **fails when the thing is
  already present** (so a later "present" gate is provably the task's doing). This is the
  anti-tautology archetype's polarity. A first-class negative preflight is **the same `scope:
  "precondition"` guardrail with inverted polarity** — and the design must **explicitly cross-reference
  `tests-fail-on-current-code` / `tests-fail-on-stubs`** so the two never diverge: a negative
  preflight is the *generalization* of that archetype to non-test artifacts (a route, a registration),
  not a competing mechanism.

### The determination heuristic (when a preflight is emittable at all)

Two predicates, with sharply different decidability — this distinction is the spine of the whole
design, and conflating them is how auto-derivation goes wrong:

- **D1 — "target pre-exists" is DECIDABLE** from the `plan-breakdown` Step-0 scan. Whether the
  artifact the task touches *already exists in the repo* (a file, a test project, a route table) is
  observable at breakdown time. A preflight is **only** emittable when D1 holds — there is nothing to
  baseline if the thing does not exist yet.
- **D2 — "modifies-not-creates" is an AUTHORING HEURISTIC ONLY.** Whether the task *modifies* the
  pre-existing thing (preflight-applicable) or *replaces / ignores* it is a judgment about intent
  that **neither the harness nor a reliable static rule can make** (this is exactly the
  undecidability that kills auto-derivation). D2 lives in the **skill's** authoring judgment, and it
  **MUST UNDER-FIRE**: when in doubt, emit no preflight. A missing preflight costs a (rare) full-budget
  burn on a broken start; a wrongly-emitted preflight false-halts a *correct* plan before it runs —
  the second is far worse (it blocks correct work), so the heuristic is biased to silence.

**This is the auto-derivation rejection, restated as a contract:** the harness consumes D1+D2 only as
an **already-authored** `scope: "precondition"` guardrail. It never *derives* applicability itself.
The skill (a human-reviewed authoring step) makes the D2 call; the harness only *runs* what was
authored and reviewed.

### Harness surface a Phase-2 implementation touches

Recorded so the scope of the deferred work is honest (every item is **forward design**, not yet in
the SSOT or the code):

- **Loader / `RawManifests` / `PlanValidator`** — parse and validate the `scope: "precondition"`
  guardrail value; collect the precondition set; a **new GR code** (next free structural code —
  **GR2027** against the live `DiagnosticCodes.cs`, whose current highest is GR2026) for a malformed
  precondition (e.g. a precondition guardrail on a non-root task, or a precondition guardrail that
  also carries an attempt-lifecycle dependency).
- **A new scheduler call site** — the preflight phase, invoked **before segment-worktree creation**
  and after the run-level pre-flight gate. Reuses the attempt-decoupled **`IReVerifier`** seam (SSOT
  §4.3 — it already runs a guardrail set against arbitrary bytes outside an attempt lifecycle, with
  cwd = the integration worktree and no `GUARDRAILS_ACTION_*` vars), which is the exact shape a
  precondition check needs. **No new guardrail machinery** — the precondition set is just a third set
  the `IReVerifier` runs, at a third site.
- **A new `TaskOutcome` / journal status / `run.json` record** — a distinct `outcome` (e.g.
  `precondition-failed`) and/or a run-level preflight record, so a human sees "the run never started:
  your baseline was broken," not a generic task failure. Distinct from `needs-human` on a task because
  *no task ran*.
- **A new exit-code branch in §7** — a preflight halt is an actionable non-green condition. It most
  naturally maps to **exit 2** (the existing "operation completed but an actionable condition was
  found" bucket), with the §7 narrative extended to name the preflight halt as one of its causes —
  rather than minting a brand-new code (KISS: the exit-code space is already a known contract;
  exit 2 already means "you have something to fix, work is durable / unstarted").

**Explicitly recorded: AUTO-DERIVATION IS REJECTED.** No part of a Phase-2 implementation may have
the harness infer pre-applicability. The harness runs an *authored* precondition set; it never
decides *which* checks should be preconditions.

---

## The BLOCKERs a Phase-2 design MUST confront (acceptance gates)

From the adversarial lens. These are **not** "considerations" — they are **gates**: a Phase-2 design
that does not have a concrete answer to each does not ship. Listed as the explicit constraints the
trigger-time work must clear.

- **(a) Cost-on-the-common-green-path / EV-inversion ("many preflights").** A preflight pays only
  when the *broken-start* case is common enough that the fail-fast saving exceeds the cost of running
  the check on *every* (usually green) run. One preflight is cheap; **N preflights run unconditionally
  up front invert the cost model on the common already-green path** — you pay N checks every run to
  save a rare full-budget burn. **Gate:** the design must show the expected value is positive *as
  preflights multiply* — bounded by the volume-control gate (§next), and ideally measured (the
  trigger requires cost evidence). A preflight phase that runs a heavy suite (full build, full test
  run) on every green run is a tax, not a saving.

- **(b) The undecidable modify-vs-create judgment → false-halt + vacuous-green classes.** Already the
  spine of the determination. **Gate:** the design must keep D2 (modifies-not-creates) **out of the
  harness entirely** (authoring-only, under-firing) and prove that no precondition guardrail can be
  auto-applied to a TDD-red / coverage gate. A false-halt (a correct plan refused before it runs) is
  the worst failure mode here — strictly worse than the burn a missing preflight costs.

- **(c) Serial-mode fast-halt — depends on #174 / #182.** A preflight's value is a *cheap* halt. The
  #174 no-op short-circuit (escalate immediately when the action changed nothing and the gate failure
  is unchanged) is **worktree-only today** (it needs `taskBase` to prove "no writes"). In serial mode
  a red doctrine-preflight gate still burns the full retry budget. **Gate:** Phase 2 must not ship the
  fast-halt promise until **#182** lands the serial-mode fix. (This is *why #182 is Phase 1* — it is a
  prerequisite for the preflight value proposition, not a tangent.)

- **(d) Negative-baseline union-inversion (the `tests-fail-on-current-code` false-fail-at-union
  lesson).** A negative preflight ("not yet present") is, by construction, a check that is **red after
  the work is done** — exactly the inversion that makes `tests-fail-on-current-code` a `local` (never
  `integration`) guardrail (#165): re-running it at a union point, after the code it tests is merged,
  false-fails. **Gate:** a `scope: "precondition"` guardrail must be **excluded from the union
  re-verify set and from the terminal gate** (it is a pre-DAG one-shot, never re-run on merged bytes),
  or it re-imports the #165/#132 union-inversion class. The precondition scope must be a *separate*
  set from the integration set precisely so it is never re-run post-merge.

- **(e) A flaky root preflight = a plan-wide single point of failure.** A doctrine preflight is a ROOT
  task every modifier depends on; a first-class preflight halts the *entire run* before it starts. A
  **flaky** preflight (a slow endpoint check, a timing-sensitive build) therefore fails the **whole
  plan** intermittently — the blast radius of a flake is maximal. **Gate:** the volume-control gate's
  "deterministic-and-cheap (NO process start)" rule is a hard constraint, not a preference — a
  preflight that starts a server / hits a network endpoint is exactly the flaky-SPOF the gate forbids.
  (This is the same lesson as the v2-deferred E2E-browser archetype, the flakiest guardrail shape —
  roadmap bet #5.)

- **(f) Naming collision — SSOT §2 already uses "pre-flight."** The run-level dirty-tree / git-repo
  check (GR2015) is **already called "a run pre-flight"** in SSOT §2 ("`guardrails validate` and a run
  **pre-flight** reject a non-git-top-level workspace"). A first-class **task/phase** "preflight" needs
  a **distinct name** to avoid two meanings of one word in one contract. **Gate:** before any SSOT
  edit, choose a non-colliding name. Candidates for the open decision (§"Open decisions"): **baseline
  phase / baseline check** (matches the doctrine archetype name and the #181 "test baseline"
  language), or **precondition phase** (matches the proposed `scope: "precondition"` value). The
  doctrine (Phase 0) sidesteps the collision entirely by using **"baseline"** in the catalogue, never
  "preflight" — preserving "pre-flight" for the run-level gate. **Recommendation: standardize on
  "baseline" for the archetype and "precondition" for the scope value; reserve "pre-flight" for the
  run-level §2 gate.**

---

## Volume-control gate (the "worth-it" rule — carry into any design)

A preflight (doctrine OR first-class) is emitted **only** when **all** of the following hold. This is
the skill-author's discipline that keeps "many preflights" from inverting the cost model (BLOCKER
(a)) and from becoming a flaky SPOF (BLOCKER (e)):

1. **Pre-exists** — the baselined thing already exists in the repo (D1, decidable from the Step-0
   scan). Nothing to baseline otherwise.
2. **Modifies** — the task *modifies* it, not *creates / replaces* it (D2, the authoring heuristic —
   **under-fire** when in doubt).
3. **Deterministic-and-cheap — NO process start.** The baseline check is a fast deterministic
   evaluation (a file grep, a static analysis, an already-fast unit subset) — **never** a server
   start, a network endpoint hit, or a flaky timing-dependent probe (BLOCKER (e)). A preflight that
   needs a process up is exactly the flaky SPOF the gate forbids.
4. **Strictly-narrower-than-the-terminal-gate.** The preflight verifies *less* than the run's
   terminal whole-repo gate (it baselines the *touched area*, not the whole repo). A preflight that
   duplicates the terminal gate's full suite is the cost-inversion of BLOCKER (a) — pay the whole gate
   twice.
5. **Shared-by-≥2-tasks.** The baseline is a precondition for **two or more** modifier tasks. A
   baseline relevant to exactly one task is just that task's own first guardrail — no preflight needed.
6. **Deduped-per-area.** One baseline per touched area, not one per task — N tasks touching the same
   area share ONE baseline node (this is the polish a first-class phase buys over N `dependsOn`
   edges; in doctrine it is one ROOT task the N tasks depend on).

If a candidate preflight fails **any** of these, it is **not emitted** — it stays an ordinary task
guardrail (or is omitted). The gate is biased to *not* emit, consistent with the under-fire rule.

---

## Trigger criteria for Phase 2 (the gate to actually do first-class)

Phase 2 stays deferred **until both** of the following are demonstrated — not argued, demonstrated:

1. **A dogfooded NON-test preflight the doctrine archetype genuinely cannot express.** A real
   breakdown of a real plan produces a preflight that is *not* a unit-test baseline (an endpoint-up
   baseline, a registration-present baseline, a route-resolves baseline) **and** that the
   no-op-ROOT-task + guardrail + `dependsOn` doctrine pattern cannot cleanly capture. If the doctrine
   pattern captures it, there is nothing first-class to build (the field would be ceremony, per the
   determination). The non-test requirement is deliberate: the test baseline (#181) is already proven
   to fit doctrine, so it can never be the trigger.

2. **Measured cost evidence that multiple preflights pay.** Real run data (journal cost
   aggregation, SSOT §7) showing that the broken-start case is common enough, and the preflight cheap
   enough, that running the precondition set up front on every (mostly green) run has **positive
   expected value as preflights multiply** (BLOCKER (a)). Intuition is not evidence; the cost cap and
   per-attempt cost are already logged in v1, so this is measurable.

**Until both fire, Phase 2 does not start.** Doctrine (Phase 0) + the #182 fix (Phase 1) are the
delivered answer to #183. This issue (#183) remains the durable home for the trigger watch.

---

## Contract / SSOT impact

**Phase 0 (doctrine): NO contract change.** The principle is expressible with the existing
`task.json`, guardrail, and `dependsOn` primitives. There is **nothing to edit in
`02-schemas-and-contracts.md`** — which is itself the proof that it is doctrine, not a feature
(invariant 4). The change lives entirely in the `plan-breakdown` catalogue and `guardrails-review`
(skill files), not the SSOT.

**Phase 1 (#182): a behavior fix, no new contract.** The #174 no-op short-circuit (SSOT §7) is
extended to fire in serial mode; the §7 narrative gains a sentence on serial-mode coverage. No new
field, status, or code — it is a fix to a documented mechanism.

**Phase 2 (DEFERRED): the forward SSOT edits — NOT YET APPLIED.** Recorded here as a forward design
so the scope is honest; they land in the SSOT **only** in the change that implements Phase 2
(invariant 4). Spelled out for that future change:

- **§4.3 (Guardrail scope)** — add a **third** value: `scope: "precondition"`. A precondition
  guardrail is collected into the run's **precondition set**, run **once** by the pre-DAG preflight
  phase against the integration worktree at user HEAD, **NEVER** part of an attempt lifecycle, **NEVER**
  re-run at a union point, and **NEVER** part of the terminal integration gate (BLOCKER (d) — it would
  union-invert). Explicitly distinct from both `"local"` and `"integration"`.
- **§3 / §3.x (task.json)** — the baseline ROOT task carries its precondition guardrail(s); a new
  validation rule (preconditions only on a root/no-dependency task; never mixed with attempt-lifecycle
  guardrails on the same task).
- **§7 (run.json / outcomes)** — a new `outcome` (e.g. `precondition-failed`) and a run-level preflight
  record: "the run did not start — baseline X was broken / unexpectedly present." Distinct from a task
  `needs-human` because no task ran.
- **§7.1 (exit codes)** — extend the **exit 2** narrative to name the preflight halt as one of its
  causes (no new code — exit 2 already means "actionable condition found").
- **Diagnostic codes** — **GR2027** (next free structural code; live highest is GR2026) for a
  malformed precondition declaration.
- **Naming** — adopt the BLOCKER (f) resolution: "**precondition**" for the scope value, "**baseline**"
  for the authoring archetype, and reserve "**pre-flight**" for the existing run-level §2 gate. The
  word "preflight" (one word, no hyphen) is avoided in the SSOT to prevent the collision.

---

## Open decisions (resolve BEFORE Phase 2 starts)

1. **The name (BLOCKER (f)).** Confirm "precondition" (scope value) + "baseline" (archetype) +
   "pre-flight" reserved for the run-level §2 gate. (Recommended above; needs the product owner's
   sign-off because it touches the SSOT vocabulary.)
2. **Precise `scope: "precondition"` semantics.** Exactly which guardrail forms may carry it
   (deterministic-only, per the cheap-no-process gate? or prompt preconditions too?); whether a
   precondition guardrail may read `GUARDRAILS_STATE_IN` (probably not — no state exists pre-DAG).
3. **Where the preflight phase runs, precisely.** Confirmed-recommended: against the **integration
   worktree at user HEAD**, after the run-level pre-flight gate, before segment-worktree creation. The
   open part: in **serial mode** (no integration worktree) it runs against the user's checkout
   read-only — confirm that is acceptable and that it is genuinely read-only there.
4. **The exit-code / journal additions.** Confirm exit 2 (not a new code) and the exact new `outcome`
   string (`precondition-failed`?).
5. **Negative-polarity expression.** Confirm a negative preflight is "the same `scope: "precondition"`
   guardrail with inverted pass/fail," cross-referenced to `tests-fail-on-current-code`, rather than a
   separate construct.

---

## Devil's-advocate self-critique

Run against my own determination, per the operating contract. The strongest counter-arguments and my
responses:

- **Counter (strongest): "Deferring first-class is just deferring the unification — you will end up
  with the doctrine no-op-ROOT-task pattern *plus* a half-considered field later, which is more total
  complexity than designing it once now."** *Response:* This is the real risk, and it is why this
  document exists — the Phase-2 design is recorded **now**, while the 4-lens context is fresh, so
  "later" inherits a vetted design, not a blank page. But designing the *contract* now (and putting it
  in the SSOT) would violate invariant 4 (a contract the harness does not yet honor) and the
  no-ceremony rule, and — decisively — we **do not yet know** whether the field buys capability over
  doctrine, because no non-test preflight has been dogfooded. Building the field before that evidence
  risks building the *wrong* field (e.g. one shaped only for the test case #181, which doctrine already
  handles). The deferral is not "decide later with no plan"; it is "execute *this* plan when evidence
  arrives." That is cheaper than building a speculative contract and reworking it.

- **Counter: "The volume-control gate's 'deterministic-and-cheap, no process start' rule (BLOCKER (e))
  rules out the most compelling preflight — 'is the endpoint already up?' — which is the exact 'plane
  on the runway' intuition #183 opens with."** *Response:* Correct, and this is an honest tension. An
  endpoint-up preflight is *exactly* the flaky SPOF that fails a whole plan intermittently. The gate
  does not forbid baselining an endpoint forever — it forbids it **as a cheap deterministic preflight**;
  a server-dependent baseline belongs in a task's own guardrail (where its flake blast-radius is one
  task with a retry budget), not in a pre-DAG phase that halts the whole run. If a dogfooded case
  proves an endpoint preflight is *worth* the flake risk, that is precisely Trigger criterion 1
  ("a non-test preflight the doctrine cannot express") — and the Phase-2 design would then have to
  confront BLOCKER (e) head-on (e.g. a bounded-retry preflight phase). The gate is the conservative
  default, not a permanent veto.

- **Counter: "Three guardrail scope values (`local` / `integration` / `precondition`) is scope-creep
  on §4.3 — the scope field was supposed to be a binary local/integration distinction."** *Response:*
  Granted that a third value adds surface. But it is the *minimal* expression of "run once, pre-DAG,
  never at a union" — the alternative (a separate `task.json` boolean + a parallel collection
  mechanism) is *more* surface and duplicates the set-collection the `scope` field already does. And
  critically, the third value is **deferred** — it lands only with Phase 2, only if the trigger fires;
  it is not speculative surface added now. If the trigger never fires, §4.3 stays binary forever.

- **Counter: "Phase 1 (#182) is being smuggled in as 'part of preflight' when it is really a
  standalone serial-mode bug fix."** *Response:* True that #182 is a standalone fix; false that it is a
  smuggle. It is named as Phase 1 precisely because the *preflight value proposition* (a cheap
  red-halt) is **dependent** on it in serial mode (BLOCKER (c)) — shipping doctrine preflights while
  the serial-mode halt still burns the full budget would deliver a half-working promise. Listing the
  dependency explicitly is the honest move; #182 ships on its own merits regardless.

---

## Implementation handoff (agent + filesTouched + sequencing)

**This plan authorizes NO Phase-2 implementation.** The handoff below is the **trigger-time** plan —
the sequence to execute *if and only if* the §"Trigger criteria" fire. What ships *now* (Phase 0 +
Phase 1) is handed off under #181 and #182, not under this document.

**Now (Phase 0 + Phase 1 — under #181 / #182, for reference, not this plan's deliverable):**
- `guardrails-skill-author` — `plan-breakdown` catalogue (the "baseline" archetype: positive +
  negative polarity, the volume-control gate, the under-fire D2 rule, cross-reference to
  `tests-fail-on-current-code`) + `guardrails-review` probe. `filesTouched`:
  `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`. (#181)
- `guardrails-harness-developer` — the #182 serial-mode no-op short-circuit fix + tests.
  `filesTouched`: `src/Guardrails.Core/Execution/**` (the short-circuit), `tests/**`. (#182)

**Trigger-time (Phase 2 — gated, do NOT start until the trigger fires):**
1. **Architect (this agent)** — re-open this plan, resolve the §"Open decisions", deliver the Phase-2
   design as a **draft PR for inline human review** (#106 design-of-record → draft-PR loop) **before**
   any implementation milestone. `filesTouched`: `docs/plans/09-preflight-first-class.md` (promote from
   DEFERRED to active) + the verbatim `docs/plans/02-schemas-and-contracts.md` edit set.
2. **`guardrails-harness-developer`** — `scope: "precondition"` parsing/validation (GR2027), the
   precondition-set collection, the pre-DAG preflight phase (reusing `IReVerifier`), the new
   `TaskOutcome` / journal record, the exit-2 branch extension. `filesTouched`:
   `src/Guardrails.Core/Loading/**`, `src/Guardrails.Core/Execution/**`, `src/Guardrails.Core/Model/**`,
   `src/Guardrails.Cli/**`, `docs/plans/02-schemas-and-contracts.md` (same change), `tests/**`.
   Sequencing: SSOT edit + parsing/validation first, then the phase + outcome + exit code.
3. **`guardrails-skill-author`** — upgrade the doctrine "baseline" archetype to emit `scope:
   "precondition"` for the qualifying cases, keeping the doctrine no-op-ROOT-task form for the rest.
   `filesTouched`: `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`.
4. **`guardrails-test-author`** — precondition-phase tests, the false-halt / union-inversion negative
   tests (BLOCKERs (b) + (d)), the serial-mode read-only assertion. `filesTouched`: `tests/**`.

Sequencing rule: **#182 (Phase 1) must land before Phase 2** (BLOCKER (c)); the architect's
draft-PR review (step 1) must complete before any harness work (step 2) starts (#106).

---

## Proposed plan-document edits

This document is itself the new plan-of-record (`docs/plans/09-preflight-first-class.md`). The
following companion edits are **proposed, not yet applied** (the lead approves, then they are made):

1. **`docs/plans/03-roadmap.md`** — no v1-milestone change. Optionally add a one-line note under the
   risk register or a "deferred designs" pointer: *"Preflight first-class — DEFERRED design of record
   in `09-preflight-first-class.md` (#183); doctrine (#181) + serial-mode fast-halt (#182) ship the
   principle; first-class is gated on a dogfooded non-test preflight + cost evidence."* (Proposed; the
   roadmap's v2-bet list is for active bets, and preflight-first-class is deferred-behind-a-trigger,
   so a pointer is lighter than a full bet entry.)
2. **`docs/plans/02-schemas-and-contracts.md`** — **NO edit now.** The forward edit set in §"Contract
   / SSOT impact" lands here **only** in the change that implements Phase 2 (invariant 4). Recorded
   here so the future change is pre-scoped.
3. **`docs/plans/README.md`** (the plan index) — add the `09-preflight-first-class.md` entry as a
   **DEFERRED design**, distinguishing it from the active plan-of-record docs. (Proposed.)

No skill or SSOT edit is made by this document — Phase 0 / Phase 1 ship under #181 / #182; this plan
records the deferred Phase-2 design and its trigger.
