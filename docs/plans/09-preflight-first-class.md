# Architecture: Preflights & guardrails, first-class at two scopes — DEFERRED design (#183)

> **Status: DEFERRED design-of-record.** This document is NOT an input to `/plan-breakdown` and
> authorizes NO implementation milestones today. It records the **target model** the product owner
> sketched — *preflights and guardrails are each first-class at two scopes, and the folder layout
> mirrors exactly that* — together with the partition's preserved polarity analysis (re-homed, not
> discarded), the diagram-rendering change the model implies, the harness phases/outcomes a future
> implementation would add, and the open questions held for the team and owner. The contract
> additions sketched in §"Contract / SSOT impact" are a **forward design**, not yet applied to
> `02-schemas-and-contracts.md` (the SSOT) — they land there only in the change that implements them,
> never before (invariant 4). **This rewrite makes NO SSOT change.**

---

## The two-scope statement (the spine of this document)

> **Preflights and guardrails are each first-class at two scopes — PLAN-LEVEL (run once, bracketing
> the whole run) and TASK-LEVEL (run per task, bracketing each task's action) — and the folder
> layout mirrors exactly that.**

A guardrail is a deterministic-first gate: *"a prompt may propose, only a deterministic gate may
certify."* A **preflight** is the same kind of gate run *before* work, to establish a baseline — so
a broken start fails fast (no token burn on a doomed attempt) and a later failure is attributed to
the work, not to a pre-existing defect. The owner's model says: **both** of these are first-class,
and **both** exist at **two scopes** that mirror the plan folder:

| Scope | Preflight folder | Guardrail folder | Runs | Against | Probe guidance (§"Live-probe guidance", ADVISORY) |
|---|---|---|---|---|---|
| **PLAN-LEVEL** (run-level, once) | `<plan>/preflights/` — the **"Full Flight Checks"** | `<plan>/guardrails/` — the **terminal / integration gate** | **once**: preflights BEFORE the DAG, guardrails AT THE END | the starting repo (preflights) / the merged result (guardrails) | deterministic **single-shot process-start is fine** (a full `dotnet test` / build is the canonical case); the skill **steers away from** network / poll / daemon / live-service probes (review WARNs) |
| **TASK-LEVEL** (per task) | `tasks/<id>/preflights/` — JIT dependency-delivery | `tasks/<id>/guardrails/` — postconditions | **per task**, bracketing the action | the task's segment worktree at `taskBase` | **prefers byte/exit checks**; the skill **steers away from** network / poll / live-service probes (review WARNs) — a cheap dependency-delivery check, not a suite run |

This is a **structural evolution** of the prior design, not a tweak. The prior PARTITION design
modelled run-level checks as **no-op tasks**: a no-op ROOT "baseline" task every first-level task
depended on, and a no-op END "integration gate" task. The owner's objection is concrete: **those
fake tasks clutter the DAG and the diagram and are awkward to author.** The new model makes the
run-level checks **first-class folders that bracket the whole DAG** — not tasks in it. The
load-bearing claims, restated so a future reader does not relitigate them:

- A **plan-level preflight** is NOT a no-op ROOT task. It is `<plan>/preflights/` — a first-class
  folder, evaluated **once, before any task is scheduled**, against the starting repo. This
  **REPLACES** the no-op ROOT baseline mechanism.
- A **plan-level guardrail** is NOT a no-op END task. It is `<plan>/guardrails/` — a first-class
  folder, evaluated **once, at the end**, on the merged plan-branch HEAD. This **REPLACES** the no-op
  END integration-gate task and **largely subsumes** today's `integrationGate` *task kind* (§3.3 of
  the SSOT) — the migration is specced below.
- A **task-level preflight** is `tasks/<id>/preflights/` — a JIT check run in the task's own segment
  worktree at `taskBase`, before that task's attempt loop. This is the PARTITION's Bucket C,
  unchanged in substance; only its name ("task-level preflight") and its first-class-folder home are
  settled here.
- A **task-level guardrail** is the existing `tasks/<id>/guardrails/` — postconditions run after the
  action. Unchanged.

The folder layout *is* the model: two folder names (`preflights/`, `guardrails/`) appearing at two
levels (the plan root, and each task). Nothing more to remember.

---

## What's being asked

GitHub #183 asks: **make "preflight" a first-class citizen** of the harness. The motivating
intuition ("make sure the plane is on the runway before flight"): for a brownfield task that
*changes* something already verified, run that verification **before** the task — so a broken
*starting* state fails fast and a post-task failure is correctly *attributed* to the task's own work
rather than to a pre-existing defect. **#181 is the canonical first instance**: before any task in a
brownfield plan runs, verify the **existing tests in the touched area already pass** (a POSITIVE
baseline); if they are already red, halt immediately with an honest "your starting point is broken."

The product owner has now sketched the **target shape** for first-class status (the hand-drawn
`Diagram.HTML for Preflight Checks`, the canonical style for this doc). Two points:

1. **Two scopes, mirroring the folders (the model above).** Run-level checks become first-class
   `<plan>/preflights/` ("Full Flight Checks") and `<plan>/guardrails/` folders that bracket the
   whole DAG — **not** no-op tasks in it. Per-task checks stay as `tasks/<id>/preflights/` and
   `tasks/<id>/guardrails/`. This supersedes (a) the no-op ROOT/END task mechanism the prior design
   shipped as doctrine and (b) the earlier "withdrawn global pre-DAG phase" — the owner is
   **reinstating** a plan-level phase, but as a first-class **folder**, not a no-op task and not a
   `scope:"precondition"` marker.
2. **A cleaner diagram (the rendering change).** Each task renders as a **self-contained container**
   with nested **Preflights** and **Guardrails** sections; DAG edges connect container→container
   directly. The current renderer's fan-out-to-guardrail-nodes and per-task `done_<task>` "Finished"
   reconvergence node are **removed**. Plan-level preflights render as a **"Full Flight Checks"
   container at the top**; plan-level guardrails as a **terminal container at the bottom**. (§"The
   diagram-rendering change".)

**Ambiguity named & narrowed.** The earlier draft's load-bearing ambiguity ("first-class" = an
authoring archetype, vs a harness-acted contract, vs harness auto-derivation) is **resolved by the
owner's model**: first-class means a **harness-acted contract** — real folders the harness evaluates
in dedicated phases — at **two scopes**. The one reading still **REJECTED outright** is
auto-derivation (the harness inferring "this task modifies, so inject a baseline"): "modifies-not-
creates" is **undecidable by the harness** and false-fails every legitimate TDD-red / coverage gate
(which is *designed* to be red before its task). The skill authors the checks; the harness runs only
what was authored. The remaining ambiguity — **exact on-disk placement of the plan-level folders**
(plan-root vs inside `tasks/`) — is recorded as an open question, with a recommendation, not silently
resolved (§"Open questions").

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

| Concern | Scope | Placement |
|---|---|---|
| **`<plan>/preflights/`** — "Full Flight Checks": a first-class run-level preflight folder, evaluated once before the DAG against the starting repo; failure halts the run before it starts (`plan-preflight-failed`); a `planHash`-keyed `planPreflights` marker makes it **resume-safe / evaluated-once** (§B1) | **plan-level** | **harness + schema** — a new pre-DAG phase + the marker + a new outcome/exit branch + a resume SKIP rule; lands in the SSOT only when implemented (invariant 4) |
| **`<plan>/guardrails/`** — the terminal/integration gate: a first-class run-level guardrail folder, evaluated once at the end on the merged HEAD (`plan-guardrail-failed`); `--revalidate-task plan:guardrails` + terminal-only resume are its human-fix path (§B2) | **plan-level** | **harness + schema** — the terminal phase + the migration from the `integrationGate` *task kind* (§3.3, GR2017 retired / GR2018 re-homed); SSOT change at implementation time |
| **`tasks/<id>/preflights/`** — per-task JIT dependency-delivery check at `taskBase` before the attempt loop (the PARTITION's Bucket C) | **task-level** | **harness + schema** — `TaskExecutor.ExecuteAsync` pre-attempt-loop slot reusing `IReVerifier`; SSOT change at implementation time |
| **`tasks/<id>/guardrails/`** — existing per-task postconditions | **task-level** | **already shipped** — no change |
| The **diagram-rendering change** (container-per-task; plan-level top/bottom containers; remove fan-out + `done_` nodes) | both | **harness (renderer §10)** — `guardrails graph` Mermaid emitter + the `source-sha256` semantic content; SSOT §10 change at implementation time |
| Authoring the checks (which preflight/guardrail goes in which folder, the polarity rules, the volume-control gate, the **advisory** live-probe guidance) | both | **skill** — `plan-breakdown` catalogue steers + `guardrails-review` WARNs (the live-probe guidance is advisory, NOT harness-enforced) |
| The harness **auto-deriving** pre-applicability ("this task modifies, inject a baseline") | — | **out of scope, permanently** — undecidable; false-fails every TDD-red gate |

Everything below the diagram line is **DEFERRED design** — recorded so the future change is
pre-scoped, applied to neither the SSOT nor the code by this document.

---

## Invariants in play

Named, with how the two-scope model bears on each:

1. **Deterministic guardrails over prompt-judges; judges never alone.** *Respected and reinforced.*
   A preflight at either scope is a *deterministic gate run earlier* — the most deterministic possible
   use of a guardrail (no action, no model, just "does the existing thing verify"). Both scopes carry
   **advisory live-probe guidance**, which is **NARROW, not a blanket "no process start"** (§"Live-probe
   guidance" states it as advice, not a harness rule): the thing the guidance steers away from is a
   **live-service / network / poll / daemon** probe — anything whose outcome depends on the state of a
   thing *outside* the committed bytes at this instant. A plan-level preflight running a full
   `dotnet test` / build (the owner's canonical "all repo unit tests pass") is **fine** — process-start
   is not the concern; it is deterministic and single-shot. A task-level preflight **prefers** a
   byte/exit check (it runs per task, before the attempt loop) and the guidance also steers away from
   network/poll. The REJECTED auto-derivation reading
   would have introduced a *harness-side judgment* ("does this task modify?") that is not deterministic
   at all — the strongest reason to reject it.

2. **Harness is the single writer of merged state; children get snapshots, write fragments.**
   *Respected at both scopes.* A plan-level preflight runs **before** any task, against the
   **integration worktree at user HEAD** (read-only — no fragment, no commit, no merged state exists
   yet). A plan-level guardrail runs on the merged plan-branch HEAD via the existing attempt-decoupled
   `IReVerifier` seam — read-only, exactly as today's union re-verify / terminal gate. A task-level
   preflight runs inside the consuming task's own segment worktree at `taskBase`, **before** the
   action, so it writes no fragment and no commit (the action is the only thing that may write one).
   No scope touches merged state.

3. **Verdicts come from files, never CLI exit codes.** *Respected.* A preflight's or plan-level
   guardrail's pass/fail is a guardrail verdict (deterministic exit / prompt verdict file) exactly as
   any guardrail — no new verdict source, just the same source evaluated at a new phase.

4. **`02-schemas-and-contracts.md` is the schema SSOT — a contract change lands there in the SAME
   change that motivates it.** *This is why the design is DEFERRED and its SSOT edits are NOT yet
   applied — and this rewrite makes NO SSOT change.* The new folders (`<plan>/preflights/`,
   `<plan>/guardrails/`, `tasks/<id>/preflights/`), the new outcomes (`plan-preflight-failed`,
   `task-preflight-failed`, `plan-guardrail-failed`), the new top-level journal sections
   (`planPreflights`, `planGuardrails`) + their resume rules, the new pre-DAG/terminal phases, the §10
   renderer change, and the `integrationGate`-task-kind migration (GR2017 retired, GR2018 re-homed) are
   all **forward design**; each lands in the SSOT **only** in the change that implements it.

5. **Honest halts — nothing marked done unverified; needs-human is a feature.** *Respected and
   extended.* A red plan-level preflight is the most honest halt there is: "your starting point is
   already broken; the run will not begin." It halts **before any token is spent** — the cheapest
   possible halt. A red plan-level guardrail halts at the end with the merged result intact on the
   plan branch — the work is durable, the human finishes the merge.

6. **Plain files, light setup — no databases, daemons, or SaaS in v1.** *Respected.* Every scope is
   plain files in the plan folder; the advisory live-probe guidance keeps even the plan-level preflight
   to deterministic byte checks (no daemon, no network). The harness reuses the existing integration
   worktree, segment worktrees, and the `IReVerifier` seam — no new infrastructure.

**The decisive invariant pairing is 1 + 5.** The plan-level preflight phase exists *to make the
honest halt cheap* (invariant 5) while staying deterministic and flake-free (invariant 1, which the
advisory live-probe guidance steers authors toward — the harness does not enforce it). The reason a
plan-level preflight is a **folder, not a no-op task**, is the same
pairing read structurally: a no-op task carries a fake action through the attempt lifecycle, the
retry budget, the merge machinery, and the diagram — ceremony that buys nothing and clutters every
view; a folder evaluated in a dedicated phase is the KISS expression of "run these checks once,
bracketing the run."

---

## The model in detail — two scopes, four folders

### Plan-level preflights — `<plan>/preflights/` ("Full Flight Checks")

The top container in the owner's sketch. A folder of deterministic-first checks the harness evaluates
**ONCE, BEFORE the DAG is scheduled**, against the **starting repo** — the integration worktree on
the plan branch at the user's HEAD, before any segment worktree exists.

- **What it asserts.** Properties of the *starting* repo, in either polarity:
  - **POSITIVE baseline** ("this repo / touched area starts from green") — e.g. *all repo unit tests
    pass* (the owner's "01-All repo unit tests pass"), the solution already builds, a route already
    resolves. If red → the start is broken; halt before spending a single task.
  - **NEGATIVE / assert-absent baseline** ("the thing this plan will introduce is absent now") — e.g.
    *`RequestId` is absent*, a new route is not yet wired. A later "it's present" plan-level guardrail
    is then provably the run's own doing. Run **once, at the start** — which is the only point a
    negative claim is true (see §"Why the negative baseline is plan-level only").
- **When.** Once, before scheduling. **On failure → the run halts before it starts** — a distinct
  outcome (`plan-preflight-failed`, the F9 split) and exit branch (§"Harness phases"); **no task runs,
  no token is spent.** This is the honest, zero-burn halt. On PASS the harness records the
  `planHash`-keyed `planPreflights` marker so resume never re-evaluates it (§B1).
- **Where.** The integration worktree at user HEAD. Read-only — no fragment, no commit.
- **Advisory guidance — the NARROW live-probe guidance (process-start is fine here).** Deterministic and
  single-shot — and **process-start is fine at plan-level**: a full `dotnet test` / `dotnet build`
  is the canonical Full Flight Check ("all repo unit tests pass"). What the guidance steers away from is
  a **live-service / network / poll / daemon** probe: no network call, no polling loop, no "is the daemon
  up?" — anything whose result depends on the state of a thing *outside the committed bytes at this
  instant*. This is **authoring advice**, not a harness rule: `plan-breakdown` steers away from it and
  `guardrails-review` **WARNs** if it sees one; the harness runs whatever was authored. The reason the
  guidance is narrow-not-blanket: a plan-level preflight halts the **whole run**, so a *flaky* probe
  there is the maximal-blast-radius SPOF — and flakiness comes from **liveness/network**, not from
  running a deterministic test suite over the committed source. "Is my endpoint up?" is best expressed
  as a byte-check on the wired source (`Select-String 'MapGet("/health")'`) rather than a live HTTP call.
  (A genuinely live check belongs in a task's own guardrail, where a flake costs one task's retry budget
  — not the whole run.) See §"Live-probe guidance" for the advisory statement.

This folder **REPLACES the no-op ROOT baseline task** mechanism. The behavior the no-op root bought
(area-start-from-green, evaluated once, every modifier "depends on" it) is exactly what a plan-level
preflight does — minus the fake task, the fake `dependsOn` edges, and the diagram clutter.

### Plan-level guardrails — `<plan>/guardrails/` (the terminal / integration gate)

The bottom container in the owner's sketch. A folder of checks the harness evaluates **ONCE, AT THE
END**, on the **merged plan-branch HEAD**, after every task has succeeded — the final whole-repo
soundness boundary.

- **What it asserts.** Whole-result postconditions: the merged repo builds, the full suite passes,
  the union invariants hold (every colliding sibling's contribution survived the merge — §4.3).
- **When.** Once, at run end, on the merged HEAD, via the existing attempt-decoupled `IReVerifier`
  seam. **On failure → a terminal halt** (`plan-guardrail-failed`, the F9 split → exit 2): the work is
  durable on the plan branch, the human finishes (via `--revalidate-task plan:guardrails` or a plain
  resume that fires terminal-only — §B2).
- **Relationship to today's `integrationGate` task kind.** This folder **REPLACES** the §3.3
  `integrationGate: true` *task*. Today the terminal gate is modelled as a task whose guardrails are
  the `scope:"integration"` set; the owner's model makes it a first-class **folder** instead, removing
  the no-op END task from the DAG and the diagram. **The migration is RESOLVED below** (§"Harness
  phases" → "Terminal phase", B3): GR2017 + the task kind retire; **GR2018's content requirement is
  re-homed onto the folder** (≥1 real integration-set re-run, not merely "non-empty"); the
  `scope:"integration"` tag survives for the per-union set. The folder is **terminal-only**; it is not
  the same object as the per-union set (which runs more often).

### Task-level preflights — `tasks/<id>/preflights/` (JIT dependency-delivery — the PARTITION's Bucket C)

A folder, sibling of the task's `guardrails/`, of deterministic checks the harness runs **in that
task's own segment worktree at `taskBase`, before the task's attempt loop.**

- **What it asserts.** *"Did my producer actually deliver the type / route / symbol I build against —
  in MY forked worktree, at the moment I run?"* A consuming task `dependsOn` a producer; the
  task-level preflight verifies, in the bytes the consumer inherited (after its producers merged in),
  that the producer's contribution is present — **before** spending an attempt building against it.
- **When.** Inside `TaskExecutor.ExecuteAsync`, **before the attempt loop** — it gates loop entry. A
  pass lets attempts proceed; a fail short-circuits to `needs-human` **without consuming a retry
  attempt** (the no-burn property), blocking **only that task and its transitive dependents**;
  independent branches keep running.
- **Where.** The consuming task's segment worktree at `taskBase` — the per-consumer intermediate state
  no plan-level phase can see (a plan-level preflight runs before any producer has run).
- **Constraints.** Positive / monotone-safe under merges (a "the type IS present" check only becomes
  *more* true as merges land — it never union-inverts the way a negative check does); the advisory
  live-probe guidance applies here too (prefer a deterministic byte check; steer away from
  process/poll/network — the skill steers, review WARNs, the harness enforces nothing); keyed to a
  `dependsOn` edge the author already drew. **The harness never derives it** — the skill authors it against the
  edge, the human reviews it.

### Task-level guardrails — `tasks/<id>/guardrails/` (existing postconditions)

Unchanged. The per-task acceptance checks run after the action, in filename sort order, all must
pass. This document changes nothing here; it is named only to complete the 2×2.

---

## Live-probe guidance (ADVISORY — applied by plan-breakdown & guardrails-review, NOT harness-enforced)

This is **authoring guidance, not a harness-enforced rule.** The provenance matters: the old "no
process start / no live-probe" wording was **agent-authored skill doctrine** (a `plan-breakdown`
catalogue heuristic first added in commit fa8ffa6 / "M6"), never a GitHub issue or an SSOT contract —
and it was self-contradictory ("NO process start" while `dotnet test`, a process start, is the
canonical example). So it was never a real harness constraint, and it is kept here only as **advice**.
The `plan-breakdown` catalogue **STEERS** check authoring away from flaky live things; `guardrails-review`
**WARNs (does NOT block)** if a check does a network / poll / daemon / live-service probe; the
**HARNESS does not enforce any of this** — it just runs whatever checks were authored.

The substance the guidance carries is unchanged: the intent is **FLAKE-FREEDOM, not process-count**. A
full `dotnet test` / build over the committed bytes is deterministic (same bytes → same result) and is
**fine at plan level** — process-start is not the concern. A genuinely *live* network probe (whose
outcome depends on the state of a thing *outside* the committed bytes at this instant) is what the
guidance steers away from, because at plan level a flake in it halts the whole run; such a live probe
belongs in a **task's own guardrail**, where a flake costs one cone's retry budget, not the run. The
guidance is stated per scope (as advice, not a rule):

| Scope | Process-start (e.g. `dotnet test` / build) | Network / poll / daemon / live-service probe | Determinism goal |
|---|---|---|---|
| **Plan-level** (`<plan>/preflights/`, `<plan>/guardrails/`) | **fine** — single-shot; the canonical case is a full suite / build over the committed bytes | **steer away (review WARNs)** — no HTTP call, no polling loop, no "is X up?" | aim for deterministic + single-shot; a re-run on identical bytes yields the identical verdict |
| **Task-level** (`tasks/<id>/preflights/`) | **prefer a byte/exit check** — it runs *per task* before the attempt loop, so a suite run here is usually the wrong tool | **steer away (review WARNs)** — same as plan-level | aim for deterministic + single-shot; positive/monotone-safe under merges |

**The property the guidance actually protects is FLAKE-FREEDOM, not process-count.** A `dotnet test` over
committed source is deterministic (same bytes → same result); a network/liveness probe is not (it
depends on a thing outside the bytes). The guidance steers away from the *non-deterministic* category, at
whatever process cost, and is indifferent to the *deterministic* category, at whatever process cost. This
is the KISS reading of invariant 1 (deterministic gates) crossed with invariant 5 (honest, non-flaky
halts): a plan-level halt on a genuinely-red suite is honest; a plan-level halt on a network hiccup is a
flake with maximal blast radius — so the guidance advises against only the latter. Because it is advice,
the enforcement is soft: the skill steers, review WARNs, the harness stays silent.

---

## B1 — Durable, resume-aware record for the plan-level phases (the load-bearing fix)

**The hole.** The prior design's no-op ROOT/END tasks got resume/skip **for free** from the journal:
a `succeeded` task is skipped on resume (§7 SSOT). The plan-level **folders** are not tasks, so they
inherit no such record. Without one, a crash **mid-DAG** followed by `guardrails run` (resume) would
**re-run the pre-DAG `<plan>/preflights/` phase against a now-partially-merged repo** — and a
**NEGATIVE baseline** ("`RequestId` absent") that was true at the run's start is now **FALSE** (an
earlier task already introduced `RequestId` onto the plan branch). The re-run would **false-halt the
resume** — the exact union-inversion the two-scope model claims to have dissolved. "Dissolved by
construction" was too strong: it is dissolved **only if the negative baseline is evaluated exactly
once across the whole run lifecycle, resume included.** A marker is what guarantees "exactly once."

**The mechanism — a `planHash`-keyed "plan-preflights passed" marker (mirrors §13's
`guardrails-review.json` shape and the §5.3 resume pre-pass that reads durable trailers).**

- **Shape.** When the pre-DAG phase **passes**, the harness records a marker in the run journal —
  a new top-level `planPreflights` section in `state/run.json` (journaled OUTSIDE the `tasks{}` map,
  because these are not tasks — see B1/F9 journal shape below), carrying `{ "status": "passed",
  "planHash": "sha256:…", "evaluatedAt": "…", "checks": [ … ] }`. The `planHash` is the **same
  `PlanHash`** the journal and the §13 review marker already use (`guardrails.json` + every
  `task.json`, newline-normalized, task-id-ordered).
- **Resume pre-pass reads it and SKIPS the pre-DAG phase.** On `guardrails run` over an existing
  journal, the resume pre-pass (the same pass that reads the plan-branch commit trailers to know which
  tasks already `succeeded`, §5.3/§7) reads `planPreflights`: if `status == "passed"` **and** its
  `planHash` matches the current `PlanHash`, the pre-DAG phase is **SKIPPED** — the plan-level
  preflights are **not re-run**. The negative baseline is therefore **evaluated exactly once** across
  the entire run lifecycle, resume included.
- **Re-run only on `--fresh`.** `--fresh` clears the run journal (§6.1), so `planPreflights` is
  cleared with it; a fresh run re-evaluates the pre-DAG phase against the (re-set) starting bytes —
  which is correct, because `--fresh` re-establishes the starting point. A `planHash` **mismatch**
  (the plan changed since the marker was written) also forces re-evaluation (the baseline may no longer
  be meaningful), exactly as a stale §13 review marker re-flags — self-invalidation by the same key.
- **Why this dissolves union-inversion HONESTLY (not "by construction").** The negative baseline is
  a claim true at exactly one point in time — the run's start. The marker is the *record that the run
  already stood at that point and passed*. Because resume reads the marker and does **not** re-evaluate,
  the negative check is never run against merged bytes — not "because negatives can't invert" (they
  can), but **because the marker guarantees the check fires once**. This replaces the earlier
  "DISSOLVED by construction" claim with "dissolved because the marker guarantees once."

*(The harness-developer review independently confirmed this hole and recommended exactly this: record
the pre-DAG pass, skip on resume, re-run on `--fresh`.)*

---

## Re-homing the PARTITION (the polarity insight is kept — its structural home moves)

The prior design's three-bucket PARTITION is **not discarded**. Its **polarity / decidability
insight survives intact**; what changes is each bucket's **structural home** — from a no-op task or a
withdrawn global phase, to a first-class folder at the right scope:

| PARTITION bucket | Polarity insight (KEPT) | Prior home | New home (this model) |
|---|---|---|---|
| **A — shared positive baseline** ("this area starts from green", the #181 case) | positive; a property of an AREA, evaluated once against the run's starting bytes | no-op ROOT task | **plan-level preflight** (`<plan>/preflights/`, positive) |
| **B — negative / assert-absent baseline** ("the thing this plan adds is absent now") | negative; true ONLY at the run's start, false ever after (union-inverts if re-run) | doctrine root no-op / deferred global one-shot | **plan-level preflight** (`<plan>/preflights/`, negative) — run once, pre-DAG |
| **C — per-task JIT dependency-delivery** ("did my producer deliver, in my worktree, now") | positive, monotone-safe under merges; per-consumer, just-in-time | the only first-class candidate (deferred) | **task-level preflight** (`tasks/<id>/preflights/`) — unchanged |
| **the integration gate** | whole-result postcondition on merged bytes | no-op END `integrationGate` task | **plan-level guardrail** (`<plan>/guardrails/`) |

Two earlier-design positions this model **SUPERSEDES**, stated plainly so the record is unambiguous:

1. It **supersedes the "no-op root/end task" mechanism.** Buckets A and B no longer ride a fake ROOT
   task; the integration gate no longer rides a fake END task. They are first-class folders bracketing
   the DAG.
2. It **supersedes the earlier "withdrawn global pre-DAG phase."** The owner is **reinstating** a
   plan-level pre-DAG phase — but as a first-class **FOLDER** (`<plan>/preflights/`), **not** a no-op
   task and **not** a `scope:"precondition"` guardrail marker. The earlier withdrawal was of a *phase
   shape that also tried to carry Bucket C*; the reinstated phase carries only A and B (the run-global
   slices), and C lives at task scope where it belongs.

### The earlier devil's-advocate concerns, re-resolved under this model

The PARTITION recorded two concerns against a plan-level phase. Both are addressed:

- **Union-inversion (a negative check re-run post-merge false-halts) → DISSOLVED, but by the B1
  marker, not "by construction."** Negative baselines (Bucket B) are **plan-level one-shots**: they run
  **once, at the run's start**, against the starting repo — never per-task, never at a union. But
  "once" is only true if **resume does not re-run them** — and a mid-DAG crash + `guardrails run`
  would, absent a durable record, re-fire the pre-DAG phase against partially-merged bytes and
  false-halt (see §B1). The dissolution is therefore load-bearing on the **`planHash`-keyed
  `planPreflights` marker** (§B1): the resume pre-pass reads it and SKIPS the pre-DAG phase, so the
  negative check is **evaluated exactly once across the whole run lifecycle, resume included.** This is
  *why* the negative baseline is plan-level-only and not a task-level preflight (a task-level negative
  check at a downstream `taskBase` would see earlier merges and false-halt — forbidden), **and** why
  the marker is not optional polish but the mechanism that makes the claim true.
- **Flaky-SPOF (a plan-level preflight halts the whole run) → now ACCEPTED as correct.** For a
  *full-flight-check*, halting the whole run on failure is the **intended** behavior — the plane does
  not leave the runway on a failed pre-flight. The mitigation is **not** to narrow the blast radius
  (that would defeat the purpose) but to **keep the check flake-free**: the **advisory live-probe
  guidance** (aim for deterministic / cheap / single-shot; process-start such as a suite run is fine;
  steer away from network / poll) keeps a plan-level preflight from ever being a *flaky* SPOF — the
  skill steers authors toward it and review WARNs, but the harness does not enforce it. A deterministic
  check that genuinely fails *should* halt the run.

### Why the negative baseline is plan-level only (the kept argument, restated)

Negative polarity is, by construction, a claim **true before the run and false after the work is
done** — the same shape that makes `tests-fail-on-current-code` a `local` (never `integration`)
guardrail (#165). A negative baseline is therefore meaningful at **one point in time only — the run's
start**. The plan-level preflight phase is exactly that point. A negative baseline is the
*generalization* of the `tests-fail-on-current-code` / `tests-fail-on-stubs` anti-tautology archetype
to non-test artifacts (a route, a registration) — a **cross-reference**, never a fork.

---

## The diagram-rendering change (the owner's point #2)

The current renderer (SSOT §10) draws each task as a flat node, **fans out** one edge to each
guardrail node, **fans in** every guardrail into a per-task `done_<task>` "Finished" node, and
connects tasks via those done-nodes (`done_A --> task_B`). The owner wants this **gone**. The new
style matches the hand-drawn `Diagram.HTML for Preflight Checks` (the canonical reference).

### New layout

- **Each TASK is a self-contained container box**, titled with the task id, holding nested
  **"Preflights"** and **"Guardrails"** sections; the individual checks are small boxes **inside** the
  container. **No separate guardrail nodes outside the box. NO per-task `done_`/"Finished"
  reconvergence node.**
- **DAG edges connect task-container → task-container directly** (clean dependency arrows). The
  fan-out-to-guardrails and the fan-in-to-`done_` are **eliminated**, and with them the reconvergence
  node entirely.
- **Plan-level preflights render as a "Full Flight Checks" container at the TOP**, flowing into the
  first task(s).
- **Plan-level guardrails render as a container at the BOTTOM** (the terminal gate), after the last
  task(s).

### ASCII mock of the new layout

```
        ┌──────────────────────────────────────────────┐
        │  Full Flight Checks   (<plan>/preflights/)    │   ← plan-level preflights, top
        │   ┌────────────────────┐  ┌────────────────┐  │
        │   │ 01 all repo unit   │  │ 02 RequestId   │  │
        │   │    tests pass      │  │    absent      │  │
        │   └────────────────────┘  └────────────────┘  │
        └───────────────────────┬──────────────────────┘
                                │
                                ▼
        ┌──────────────────────────────────────────────┐
        │  01-scaffold-solution                         │   ← task container
        │   Preflights                                  │
        │     ┌──────┐  ┌──────┐                        │
        │     │  01  │  │  02  │                         │
        │     └──────┘  └──────┘                        │
        │   Guardrails                                  │
        │     ┌────────────────┐  ┌──────┐              │
        │     │ 01 sln         │  │  02  │              │
        │     │    registers   │  └──────┘              │
        │     └────────────────┘                        │
        └───────────────────────┬──────────────────────┘
                                │   (container → container, no done_ node)
                                ▼
        ┌──────────────────────────────────────────────┐
        │  02-unit-tests                                │   ← task container
        │   Guardrails                                  │
        │     ┌────────────────┐  ┌────────────────┐    │
        │     │ 01 test build  │  │ 02 test fail   │    │
        │     └────────────────┘  └────────────────┘    │
        └───────────────────────┬──────────────────────┘
                                │
                                ▼
        ┌──────────────────────────────────────────────┐
        │  Terminal Gate   (<plan>/guardrails/)         │   ← plan-level guardrails, bottom
        │   ┌────────────────────┐  ┌────────────────┐  │
        │   │ whole-repo build   │  │ full suite     │  │
        │   └────────────────────┘  └────────────────┘  │
        └──────────────────────────────────────────────┘
```

(The hand-drawn `Diagram.HTML for Preflight Checks` is the canonical style; this ASCII mock is its
text rendering.)

### Mermaid implementation implication

The current flat-node + `done_` renderer **must change**:

- A **task container** is a Mermaid `subgraph task_<id>["<id>"]` holding two nested subgraphs
  (`subgraph <id>_preflights["Preflights"]`, `subgraph <id>_guardrails["Guardrails"]`), each
  containing the individual check nodes. The plan-level containers are two more top-level subgraphs
  (`subgraph plan_preflights["Full Flight Checks"]`, `subgraph plan_guardrails["Terminal Gate"]`).
- **Edges go container→container** — but with a **rendering-reality caveat (invisible anchor nodes).**
  Mermaid does **NOT** faithfully render a `subgraph --> subgraph` (container→container) edge: pointing
  an edge at a subgraph id is unreliable across versions and often draws to an arbitrary interior node
  or not at all. The faithful technique is an **invisible anchor node** inside each container
  (`<id>_anchor[" "]:::invisible`, zero-content, `classDef invisible` with no fill/stroke) and drawing
  the DAG edges **anchor→anchor** (`task_A_anchor --> task_B_anchor`). This **partly reintroduces a
  node family** — so the "no nodes, pure containers" promise in the owner's sketch is **not literally
  achievable**; it becomes "no *visible* free nodes; one invisible anchor per container carries the
  edge." This caveat must be stated in the doc and the SSOT §10 change, and **the renderer must be
  prototyped against the bundled Mermaid version BEFORE building** (the anchor technique is
  version-sensitive; a golden that passes on one Mermaid version can regress on another).
- **Does a task-level preflight render its gated `dependsOn` edge?** Decided explicitly (it was
  ambiguous): **YES — the container→container `dependsOn` edge is still drawn.** A `tasks/<id>/preflights/`
  check gates a `dependsOn` edge (the consumer verifies the producer delivered); collapsing the
  producer into a container must NOT erase that edge, or the diagram loses the dependency the preflight
  exists to guard. The preflight renders as a check node *inside* the consumer's Preflights subgraph
  **and** the `task_producer_anchor --> task_consumer_anchor` edge remains. (Rejected alternative:
  re-route the edge to originate from the preflight node — that reintroduces a free-node edge family
  and couples edge routing to preflight presence.)
- The fan-out edges (`task --> guardrail`) and the `done_<id>` nodes are **deleted**. Guardrails (and
  preflights) are no longer free nodes with their own dependency edges; they are **contents of the
  task's subgraph**, not participants in the DAG. (The invisible anchors are the *only* free-node
  family that survives, and they carry edges, not labels.)
- **`classDef`s** color the four kinds (task container, preflight check, guardrail check, plan-level
  container) distinctly, plus one `classDef invisible` for the anchors; retry/feedback edges remain out
  of scope.
- **`source-sha256`** (the staleness key) must fold the new structure into its semantic content
  (container membership + nested check labels + container→container edges), so a freshness check still
  fires when any of them changes — and stays stable across irrelevant reorderings (subgraphs and their
  contents sorted ordinal). **CRITICAL — it MUST fold the PLAN-LEVEL folder checks too.** The
  `<plan>/preflights/` and `<plan>/guardrails/` checks are **not in `tasks{}`**; if the hash is computed
  only from task structure (as today's §10 hash effectively is), then **editing a terminal-gate check
  leaves the diagram falsely fresh** — `graph --check` reports "up to date" while the drawn Terminal
  Gate container no longer matches the folder. The hash's semantic content must therefore include the
  plan-level containers' labels + nested check labels. **Required test (added to the handoff):** *edit a
  `<plan>/guardrails/` check → `graph --check` reports stale (exit 2).*

This is a **non-trivial renderer rewrite** — the node/edge model changes from "tasks + guardrails +
done-nodes, all free nodes" to "containers with nested checks, invisible anchors carrying
container→container edges." The anchor-node reality and the version-sensitivity mean the renderer must
be **prototyped against the bundled Mermaid version before the build commits to the container model.**
Specced here as a deferred SSOT §10 change; the implementation handoff names it.

---

## Harness phases to spec (still deferred — design only)

Three phases a future implementation would add, plus the `IReVerifier` reuse. **Every item is forward
design, not yet in the SSOT or the code.**

### Pre-DAG phase — evaluate `<plan>/preflights/` once

- **When.** After load/validate, **before the Scheduler builds waves** — the first thing a `run`
  does after it has a valid plan.
- **Where.** The integration worktree on the plan branch at the user's HEAD (the starting bytes),
  read-only. **Mode interaction — RESOLVED:** the plan-level phase is inherently **serial and
  single-shot**, so it runs **once on the integration worktree regardless of `maxParallelism`**, never
  sharded across segment worktrees; serial-mode (`maxParallelism: 1`) behavior is identical. This
  requires the `IReVerifier` to be wired unconditionally (§"Serial-mode reality").
- **Failure → halt before scheduling.** A distinct outcome **`plan-preflight-failed`** (the F9 split —
  see below); **no task runs.** Exit code: a plan-level preflight failure is an **actionable,
  work-not-started** halt → **exit 2** (the same class as needs-human: "actionable condition found;
  nothing was spent / started"). The journal records the result in the top-level `planPreflights`
  section (§B1), distinct from any task. Exit 2 is **reused**, not a new code (recommended and now
  settled — consistent with the existing "actionable condition" semantics).
- **Auto-derivation rejection holds.** The harness runs the authored `<plan>/preflights/` checks; it
  never infers which checks should be preflights.

### Terminal phase — evaluate `<plan>/guardrails/` once on the merged HEAD

- **When.** After every task has settled green, on the merged plan-branch HEAD — replacing the
  terminal `integrationGate` *task* run.
- **Where.** The integration worktree, via the existing attempt-decoupled `IReVerifier` seam (the same
  seam today's terminal gate and union re-verify use). **No new guardrail-runner machinery.**
- **Migration from the `integrationGate` task kind (§3.3) — B3: keep the CONTENT teeth.** Today GR2017
  requires a multi-leaf/fan-in plan to declare exactly one `integrationGate: true` sink, and **GR2018
  requires that sink to carry ≥1 `scope:"integration"` guardrail** ("a gate with none verifies
  nothing"). Under the model the terminal checks move into `<plan>/guardrails/`. **The migration is now
  RESOLVED normatively (was an open question), and B3 is the reason it can't be a soft resolution:**
  - **REPLACE the terminal-gate TASK with `<plan>/guardrails/`.** The `integrationGate: true` task
    kind and **GR2017** (the "declare exactly one sink" rule) retire — the no-op END task is exactly
    the clutter the owner is removing.
  - **PRESERVE GR2018's content requirement — do NOT weaken it to "non-empty folder."** A "the folder
    isn't empty" rule lets a tautological `exit 0` file pass the terminal gate and **certify nothing** —
    the precise failure GR2018 exists to prevent. The replacement rule keeps the teeth: **"a
    multi-leaf/fan-in plan MUST have a `<plan>/guardrails/` carrying ≥1 deterministic check that
    actually re-runs the integration set"** (the whole-repo build / full suite / a union invariant) —
    not merely a present file. GR2018 is **re-homed onto the folder**, not retired: same content
    obligation, new carrier. (An implementation detail for the SSOT change: the check may reuse the
    §4.3 `scope:"integration"` tag as the "counts toward the terminal gate" marker on the folder's
    files, or introduce a folder-scoped equivalent; either way the *content* obligation — ≥1 real
    integration-set re-run — survives.)
  - **KEEP `scope:"integration"` as the per-union set.** GR2018 (the *sink-task* rule) retires, but the
    `scope:"integration"` **tag survives for unions**: the §4.3 per-union re-verify (at intermediate
    fan-in / non-FF integration points, `SettleAsync`) still runs the integration-scoped guardrail set,
    unchanged. The tag is not coupled to the terminal task kind; only the *terminal-sink* obligation
    moved to the folder.
  - **Terminal folder vs per-union set — stated plainly (harness-dev's steer).** `<plan>/guardrails/`
    is **TERMINAL-ONLY** — the final whole-HEAD gate, evaluated once at run end. It is **NOT** the same
    object as the per-union `scope:"integration"` set: the per-union set is run **more often**
    (at every union, during the run) and keeps using the `scope:"integration"` tag; the terminal
    folder is run **once, last**. Recommended relationship: the terminal folder's checks are typically
    a **superset-or-equal** of the integration set (build + full suite + any whole-result invariant),
    but they are declared **in the folder**, and the per-union set is declared **by the tag** — two
    declarations, one shared spirit, no forced identity. (See open question on whether an author may
    point the terminal folder AT the tagged set to avoid duplication — kept open, but the *default* is
    they are authored independently.)
- **Failure → terminal halt** (`plan-guardrail-failed` → exit 2). The work is durable on the plan
  branch; the merge-collision attribution (#175) carries over (it is a property of the gate failure,
  not of where the gate lives).
- **Relationship to per-union re-verify.** The §4.3 per-union integration-set re-verify is unchanged —
  it runs at every fan-in / non-FF integration *during* the run, keyed by the `scope:"integration"`
  tag. `<plan>/guardrails/` is the **final, terminal-only** whole-HEAD gate (see B3 above for the
  normative terminal-vs-per-union statement).

### B2 — The terminal gate's human-fix / revalidate path + the DAG-green/terminal-red resume rule

A `<plan>/guardrails/` folder **has no task id**, so two mechanisms that today rely on a task id have
nothing to target. Both are designed here (they were the second load-bearing hole):

**(a) Revalidate after a hand-fix — a synthetic stable id, NOT a new verb.** Today `--revalidate-task
<id>` (§7.1) re-confirms a hand-fixed `needs-human` task WITHOUT re-running the DAG or spawning an
agent. When the *terminal gate* fails, a human fixes the merged HEAD by hand and wants the same
"confirm the gate now passes, don't re-run everything" affordance — but there is no task id to pass.

**Decision: mint a synthetic stable id for the plan-level guardrail folder and let the EXISTING
`--revalidate-task` accept it** — rather than a dedicated `--revalidate-terminal` verb.
- The reserved id is **`plan:guardrails`** (the `:` is already disallowed in a real `stableId`/folder
  id by the §3 `^[a-z0-9][a-z0-9._-]*$` rule and reserved for synthetic identities — §11's merge uses
  the same `folder:<name>` convention, so `plan:guardrails` can never collide with a real task).
- `guardrails run --revalidate-task plan:guardrails` runs **only the `<plan>/guardrails/` checks**
  against the current merged HEAD (worktree-mode caveat identical to §7.1: an in-place fix is only
  visible where the checks run — for the terminal gate that is the **integration worktree**, which the
  harness owns, so this verb points the `IReVerifier` at the integration worktree, not the user's
  checkout). Pass ⇒ the terminal phase settles green, run exits 0; fail ⇒ still `plan-guardrail-failed`,
  exit 2 — the same settle contract §7.1 gives a task.
- **Why reuse over a new verb:** `--revalidate-task` already means "re-run one gate's checks, no
  action, journal a synthetic settle, no DAG re-run" — which is exactly the terminal-gate need. A new
  verb would duplicate that semantics for one more id shape. The synthetic id is the smaller contract
  (KISS): one reserved constant, zero new CLI surface, and it composes with the eligibility/exit-code
  rules §7.1 already specs. (The corresponding plan-level *preflight* re-check reuses the same idea
  with the reserved id **`plan:preflights`**, should a human want to re-confirm a hand-fixed starting
  state without a full `--fresh`.)

**(b) Resume rule for "DAG all-green but terminal folder red."** Today resume (§7) has a rule for each
*task* status, but no rule for the state *"every task `succeeded`, yet the terminal `<plan>/guardrails/`
gate is red."* Named explicitly:
- The terminal-phase result is journaled in a new top-level `planGuardrails` section of `run.json`
  (outside `tasks{}`, mirroring `planPreflights` — see B1/F9 journal shape). On a red terminal gate it
  records `{ "status": "failed", "planHash": "…", "failedChecks": [ … ] }`.
- **On `guardrails run` (resume):** every task is `succeeded` ⇒ **all tasks skip** via the existing
  resume rule (no task re-runs, no attempt burned); the resume pre-pass then reads `planGuardrails` and,
  seeing `status == "failed"` (and a matching `planHash`), **RE-FIRES ONLY the terminal phase** on the
  current merged HEAD. Name for this transition: **terminal-only resume** (the DAG is settled; only the
  whole-HEAD gate re-runs). A `planHash` mismatch (the plan changed) instead falls back to a normal
  resume that may re-schedule affected tasks.
- This is the resume-side complement of (a): `--revalidate-task plan:guardrails` is the *explicit*
  single-shot re-check; **terminal-only resume** is what a plain `guardrails run` does automatically
  when the DAG is green and only the terminal gate is red.

### Task-level preflight slot — `tasks/<id>/preflights/` (the PARTITION's harness-feasibility note, carried forward)

- **Slot-in point.** `TaskExecutor.ExecuteAsync`, **before the attempt loop** — it gates loop entry.
- **Runner reuse.** Reuses the existing attempt-decoupled `IReVerifier` seam (it already runs a
  guardrail set against arbitrary bytes outside an attempt lifecycle, cwd = a given worktree, no
  `GUARDRAILS_ACTION_*` vars) — here pointed at the consumer's `taskBase`. **No new machinery.**
- **Failure.** Short-circuits to `needs-human` **without consuming a retry attempt** (no-burn), in
  **both** serial and worktree mode (the fail-fast is structural, not budget-dependent). Outcome
  **`task-preflight-failed`** (the F9 split — a *distinct* outcome from the plan-level
  `plan-preflight-failed`, because the consumer-side blast radius differs: a plan-level preflight halts
  the WHOLE run with zero tasks; a task-level preflight blocks only ONE cone). It is a **`TaskOutcome`
  inside `tasks{}`** (it *is* a per-task result), unlike the plan-level outcomes which live outside
  `tasks{}`. Blocks only the task + its transitive dependents via the existing scheduler closure;
  independent branches keep running.
- **Serial-mode wiring is a first-class REQUIREMENT here, not an afterthought** — see §"Serial-mode
  reality" below; the runner this slot reuses (`IReVerifier`) is **NULL in serial mode today**.
- **Effort: S–M** for the task-level slot; **M** for the plan-level phases + the §10 renderer rewrite.

### New outcomes / exit codes / journal entries (summary — F9 split + journal shape RESOLVED)

The prior draft overloaded a single `preflight-failed` for both scopes. **That is split (F9):** a
plan-level preflight halts the WHOLE run (zero tasks ran); a task-level preflight blocks only ONE cone.
A consumer that cannot tell them apart cannot render the right halt. Three distinct outcomes:

- **`plan-preflight-failed`** — the pre-DAG `<plan>/preflights/` phase failed → halt **before
  scheduling**, no task runs, zero tokens spent → **exit 2**. Journaled in the top-level
  **`planPreflights`** section (outside `tasks{}`).
- **`task-preflight-failed`** — a `tasks/<id>/preflights/` slot failed → **`needs-human`** for that
  task's cone, **no attempt burned**; independent branches keep running → **exit 2**. Journaled as a
  **`TaskOutcome` inside `tasks{}`** (it is a per-task result).
- **`plan-guardrail-failed`** — the terminal `<plan>/guardrails/` gate failed on the merged HEAD →
  terminal halt, work durable on the plan branch → **exit 2**. Journaled in the top-level
  **`planGuardrails`** section (outside `tasks{}`).

**Decision — distinct outcome names, not a shared name + a `scope` field.** A shared `preflight-failed`
+ a mandatory `scope: "plan" | "task"` consumers must branch on was considered. Rejected: distinct
names are the smaller cognitive contract (a reader sees `plan-preflight-failed` and immediately knows
the whole run halted), match how §7 already names outcomes by their *situation* (`output-cap`,
`max-turns`, `rate-limited`) rather than by a discriminator field, and remove the failure mode where a
consumer forgets to branch on `scope` and renders a plan-level halt as a task-level one. The plan-level
scope is *also* encoded structurally (the result lives outside `tasks{}`), so no field is needed.

**Journal shape (round-trips losslessly — the additive top-level sections):**

```jsonc
{
  "version": 1,
  "runId": "…", "planHash": "sha256:…", "nextMergeSequence": 3,
  "planPreflights": {                      // NEW, top-level, OUTSIDE tasks{} — B1/F9
    "status": "passed",                    // passed | failed  (absent ⇒ not yet evaluated)
    "planHash": "sha256:…",                // the marker key — SKIP-on-resume iff it matches (§B1)
    "evaluatedAt": "…",
    "checks": [ { "name": "01-all-repo-tests-green", "pass": true } ]
  },
  "planGuardrails": {                      // NEW, top-level, OUTSIDE tasks{} — B2/F9
    "status": "failed",                    // passed | failed  (absent ⇒ not yet reached)
    "planHash": "sha256:…",
    "failedChecks": [ { "name": "02-full-suite", "reason": "3 tests red" } ]
  },
  "tasks": {
    "07-consume-widget": {
      "status": "needs-human",
      "attempts": [ { "attempt": 1, "outcome": "task-preflight-failed", /* … */ } ]
    }
    // … other tasks …
  }
}
```

- **Additive & lossless.** `planPreflights` / `planGuardrails` are new top-level keys; an older reader
  ignores them, a plan without the feature omits them — the existing `tasks{}` shape is untouched, so
  the journal round-trips losslessly with or without the sections.
- **`task-preflight-failed` stays a `TaskOutcome`** in the §7 attempt-outcome enum, alongside
  `guardrail-failed` / `action-failed` / `output-cap` / `max-turns` / `rate-limited`.
- **Exit code — reuse 2, no new code.** All three halts are the existing exit-2 "actionable condition;
  work durable/unstarted" class (§7.1). Settled, not "confirm later."

### Serial-mode reality — the `IReVerifier` runner must be wired UNCONDITIONALLY (design requirement)

**The trap.** The attempt-decoupled `IReVerifier` seam — which the pre-DAG phase, the terminal phase,
and the task-level preflight slot all reuse — is **NULL in serial mode today**: it is only constructed
when `maxParallelism > 1 && git` (worktree mode), because its only current caller (the §4.3 per-union
re-verify) never fires in serial mode (serial mode forms no unions). If the plan-level and task-level
preflight phases reuse a runner that is null at `maxParallelism: 1`, they would **silently no-op in
serial mode** — and a silent no-op on a *preflight* is exactly a hidden false-halt-avoidance: the
honest halt the phase exists to produce never fires. **Design requirement, recorded as first-class (not
an afterthought):** the `SchedulerFactory` composition root MUST wire the `IReVerifier` (or an
equivalent attempt-decoupled guardrail runner) **unconditionally** — in serial mode too — so the three
preflight/terminal phases run in **both** modes. The harness-developer handoff lists this as a
prerequisite sequenced BEFORE the phases that depend on it. This is where a false-green could hide, so
the test matrix (handoff step 4) exercises every phase in **serial AND worktree mode**.

---

## Open questions

This hardening pass **CLOSED** the load-bearing ones. The record below states what is now closed (with
the resolution) and what remains open for the owner.

### CLOSED by this pass

- **CLOSED — `integrationGate` replacement (B3).** REPLACE the terminal-gate *task* + retire GR2017;
  **PRESERVE GR2018's content requirement** re-homed onto the folder ("≥1 deterministic check that
  actually re-runs the integration set" — NOT "non-empty folder"); **KEEP `scope:"integration"` as the
  per-union set**. `<plan>/guardrails/` is **terminal-only**; the per-union re-verify keeps using the
  tag. (§B3 / terminal phase.) *No coexistence window — full replacement of the task kind, content
  teeth kept.*
- **CLOSED — serial-mode wiring.** The `IReVerifier` runner is wired **UNCONDITIONALLY** in
  `SchedulerFactory` (serial mode too), sequenced before the phases that reuse it; every phase tested
  in serial AND worktree mode. (§"Serial-mode reality".)
- **CLOSED — terminology.** "**Full Flight Checks**" is KEPT as the user-facing / diagram label for
  plan-level preflights; the on-disk folder is `preflights/`. "Terminal Gate" is the label for
  `<plan>/guardrails/`.
- **CLOSED — journal shape + outcome split (B1 / F9).** Plan-level results live in **additive
  top-level `planPreflights` / `planGuardrails`** sections (outside `tasks{}`, round-trip lossless);
  task-level stays a `TaskOutcome` inside `tasks{}`. Outcomes split into **`plan-preflight-failed` /
  `task-preflight-failed` / `plan-guardrail-failed`** (distinct names, not a shared name + `scope`
  field), all reuse **exit 2**. (§"New outcomes … F9 split".)
- **CLOSED — negative-baseline safety (B1).** A **`planHash`-keyed `planPreflights` marker** records
  the pre-DAG pass; the resume pre-pass reads it and **SKIPS the pre-DAG phase on resume**; re-run only
  on `--fresh` or a `planHash` mismatch. The negative baseline is **evaluated exactly once across the
  whole run lifecycle** — union-inversion dissolved *because the marker guarantees once*, not "by
  construction." (§B1.)
- **CLOSED — terminal human-fix / revalidate + resume (B2).** Synthetic id **`plan:guardrails`**
  accepted by the existing `--revalidate-task` (no new verb); plus **terminal-only resume** — DAG all
  `succeeded` ⇒ all tasks skip, only the terminal phase re-fires. (§B2.)
- **CLOSED — Mermaid container edges.** Container→container edges require **invisible anchor nodes**
  (the "no nodes" promise is softened to "no *visible* free nodes"); the renderer must be **prototyped
  against the bundled Mermaid version before building**; a task-level preflight **still renders its
  gated `dependsOn` edge**. (§"Mermaid implementation implication".)
- **CLOSED — `source-sha256` folds plan-level checks.** The staleness hash includes the plan-level
  folder checks; a required test asserts *edit `<plan>/guardrails/` check → `graph --check` reports
  stale*. (§"Mermaid implementation implication".)

### STILL OPEN — for the owner

1. **Exact on-disk placement of the plan-level folders — THE ONE STILL OPEN.** The owner pointed at the
   `…/texttools/tasks` level. Two options:
   - **(A) Plan-root siblings of `tasks/`** — `<plan>/preflights/` and `<plan>/guardrails/` sit
     alongside `guardrails.json`, `state/`, `tasks/`. **RECOMMENDED:** the model's whole point is that
     these are **plan-level**, not task-level; placing them at the plan root makes the scope visible on
     disk (plan-level folders at the plan root; task-level folders under each task). It also keeps the
     `tasks/` directory a pure list of tasks, and avoids a collision with a task literally named
     `preflights`/`guardrails`.
   - **(B) Inside `tasks/`** — e.g. `tasks/preflights/`, `tasks/guardrails/`. Closer to where the owner
     pointed, but it mixes a plan-level concern into the per-task directory and risks the name
     collision above.
   - **Recommendation: (A) plan-root. FLAGGED OPEN for the owner** — the owner explicitly pointed at
     the `tasks/` level, so this needs an explicit owner call before the SSOT §1 layout is written.
2. **(Minor, owner-optional) May an author POINT the terminal `<plan>/guardrails/` folder at the tagged
   `scope:"integration"` set** to avoid re-declaring the same build/suite in two places? The **default
   is they are authored independently** (B3); a "reference the tagged set" convenience is a possible
   later affordance, not required for v1. Recorded, not blocking.

---

## Devil's-advocate self-critique

Run against my own two-scope model, per the operating contract. The strongest counter-arguments and
my responses:

- **Counter (strongest): "Two scopes × two kinds = four folders is more surface, not less. You
  removed two no-op tasks and added two plan-level folders + a renderer rewrite + two new
  phases + two new outcomes. The no-op-task mechanism was *zero new contract* — fully expressible
  with existing primitives. This model trades a clutter problem for a contract-expansion problem, and
  invariant 4 says contract expansion is the expensive kind."** *Response:* Conceded that this is real
  new contract where the prior design was none — and that is the honest cost the owner accepted in
  exchange for the model. But the trade is favorable on two axes. (1) **The no-op-task mechanism was
  "expressible" but dishonest in the views that matter**: a no-op ROOT task is a *fake task* in the
  DAG, the diagram, the journal, the retry machinery, and the resume record — it cost zero *schema*
  but polluted every *runtime surface*. The owner's objection is precisely that the cost landed where
  users look. (2) **The contract added is small and mostly reuse**: two folder names (mirroring the two
  that already exist), a pre-DAG phase and a terminal phase that both **reuse `IReVerifier`** (no new
  runner), and two outcomes that **reuse exit 2** (no new code). The genuinely new build is the §10
  renderer rewrite — which the owner is asking for independently because the current diagram is
  unreadable. So the net new *capability* surface is modest; the visible *clutter* removed is large.
  Still, the counter is right that this is no longer "doctrine, no contract" — it is a real first-class
  build, deferred, which is why the SSOT edits are forward-only and gated on owner sign-off of the open
  questions.

- **Counter: "Reinstating a plan-level pre-DAG phase resurrects exactly what was *withdrawn*. The
  PARTITION withdrew the global pre-DAG phase for stated reasons; you are walking it back."** *Response:*
  Partly conceded, and the record says so plainly. But the withdrawal was of a phase shape that **also
  tried to carry Bucket C** (per-task dependency-delivery) — and *that* is what the phase
  structurally could not express (a pre-DAG phase runs before any producer). The reinstated phase
  carries **only A and B** (the run-global slices it *can* express), and C lives at **task scope**.
  The two concerns that drove the withdrawal are re-resolved, not ignored: union-inversion is dissolved
  (negative baselines are one-shot at the start), and flaky-SPOF is accepted-as-correct-for-a-full-
  flight-check + kept-flake-free by the advisory live-probe guidance. So this is a *scoped* reinstatement
  with the failure modes addressed, not an unconditioned walk-back.

- **Counter: "The live-probe guidance steers away from the most compelling preflight — 'is my
  dependency's endpoint *up*?' — the literal 'plane on the runway' intuition #183 opens with."**
  *Response:* Correct for the *live* reading, and an honest tension — but the guidance is now **narrow**:
  it steers away only from the network/poll/daemon/live-service category, and treats process-start as
  **fine** (a full `dotnet test` / build is the canonical Full Flight Check). So the guidance does not
  discourage the deterministic heavy checks; it discourages only the *flaky* ones. A live endpoint probe
  in a *plan-level* preflight is the maximal-blast-radius flake — it halts the whole run on a network
  hiccup — so the guidance steers away from it (review WARNs, the harness does not block); the intuition
  is preserved as a **byte-check on the wired source** (the route is `MapGet`-registered in the committed
  file), deterministic and single-shot. A genuinely *live* check belongs in a task's own guardrail, where
  a flake costs one task's retry budget, not the run. (§"Live-probe guidance" states this as advice, not
  a harness rule.)

- **Counter (F8 — the cheaper alternative, RECORDED AS CONSIDERED-AND-REJECTED): "You don't need the
  four-folder model at all. Keep the no-op tasks; only rewrite the RENDERER to collapse each task + its
  guardrails into a container. You get the diagram declutter — the owner's visible pain — at near-zero
  risk, with zero new contract, zero new phases, zero new outcomes, and no B1/B2/B3 holes to design
  around."** *Response:* This is the strongest cheap alternative and it is **weighed, not ignored** — it
  genuinely buys the *rendering* win at a fraction of the cost, and if declutter were the only goal it
  would win. It is **rejected** for one decisive reason the skill-author's evidence supplies: the no-op
  task is **awkward to AUTHOR, not merely to render.** A no-op ROOT/END task carries **four artifacts**
  (a `task.json`, a no-op `action.*`, a `guardrails/` folder, and the `dependsOn` fan-out every
  first-level task must draw to it), plus the **fragile no-op-ness** the #174/#182 short-circuit exists
  to paper over (a no-op action that must reliably change nothing so the short-circuit can fire). The
  renderer-only fix leaves **all** of that authoring burden in place — it hides the clutter in the
  diagram while the skill still has to emit and maintain the fake tasks, and `guardrails-review` still
  has to reason about them. The owner asked to **eliminate** the no-op tasks, not just to stop drawing
  them. So: **considered and rejected — the authoring win (fewer artifacts, no fan-out, no fragile
  no-op) justifies the full model; the renderer-only fix addresses a symptom, not the cause.** (This is
  recorded so a future reader sees the cheaper path was on the table and why it lost.)

- **Counter: "Removing the `done_<task>` reconvergence node loses information — the diagram no longer
  shows *when a task is finished* before its dependent starts."** *Response:* The `done_` node never
  carried real information; it was a rendering artifact of treating guardrails as free nodes that had to
  reconverge somewhere. Under the model, guardrails live **inside** the task container, so "the task is
  finished" is just "the container's checks passed" — a property of the container, drawn as the
  container→container edge leaving it. The semantics ("A done, now B may start") are carried by the
  direct edge; the node was ceremony. The caption already disclaims that the diagram is structure-only,
  not a one-pass timeline, so no temporal information is lost.

---

## Implementation handoff (agent + filesTouched + sequencing)

**This plan authorizes NO implementation.** The handoff is the **trigger-time plan** — executed if and
only if the owner approves the model and resolves the **one remaining open question (#1 placement)**.
The B1/B2/B3, serial-mode, journal-shape, outcome-split, terminology, and `integrationGate`-migration
questions are **closed by this pass** (§"Open questions"). Sequencing is gated on the design-of-record
draft-PR review (#106): this doc lands as a draft PR for inline human review **before** any milestone
starts.

1. **Architect (this agent)** — once the placement question is resolved, deliver the active design + the
   verbatim SSOT edit set as a **draft PR for inline review** (#106). `filesTouched`:
   `docs/plans/09-preflight-first-class.md` (promote DEFERRED → active) +
   `docs/plans/02-schemas-and-contracts.md` (the edit set: §1 layout, §3.3 migration incl. GR2018
   re-homing + GR2017 retirement, §7 the three new outcomes + `planPreflights`/`planGuardrails`
   sections + resume rules, §7.1 `plan:guardrails` revalidate id + exit-2, §10 renderer + anchor nodes +
   plan-level-checks-fold-into-`source-sha256`).
2. **`guardrails-harness-developer`** — the three phases + the §10 renderer. Sequencing:
   **(0) PREREQUISITE — wire `IReVerifier` UNCONDITIONALLY in `SchedulerFactory`** (serial mode too;
   §"Serial-mode reality") — every later phase depends on it. Then SSOT edit + folder
   parsing/validation; then the pre-DAG phase (`<plan>/preflights/` evaluation + the `planPreflights`
   marker + `plan-preflight-failed` + exit-2 branch + the **resume SKIP-on-marker** rule); then the
   terminal phase (`<plan>/guardrails/` on merged HEAD, `IReVerifier` reuse, GR2017 retirement +
   **GR2018 re-homed as the "≥1 real integration-set re-run" folder rule**, `plan-guardrail-failed`,
   the `plan:guardrails` revalidate id, the **terminal-only resume** rule); then the task-level
   preflight slot (`TaskExecutor.ExecuteAsync` pre-loop, `IReVerifier` at `taskBase`,
   `task-preflight-failed` as a `TaskOutcome`); then the §10 renderer rewrite (container subgraphs,
   **invisible anchor nodes**, anchor→anchor edges, remove fan-out + `done_`, **fold BOTH task and
   plan-level checks into `source-sha256`**) — **prototype against the bundled Mermaid version first.**
   `filesTouched`: `src/Guardrails.Core/Loading/**`, `src/Guardrails.Core/Execution/**`,
   `src/Guardrails.Core/Model/**`, the graph renderer under `src/Guardrails.Core/**`,
   `src/Guardrails.Cli/**`, `docs/plans/02-schemas-and-contracts.md` (same change), `tests/**`.
3. **`guardrails-skill-author`** — teach `plan-breakdown` to emit the four folders (plan-level
   preflights/guardrails; task-level preflights for the dependency-delivery case keyed to a `dependsOn`
   edge), the polarity rules (positive/negative at plan-level; positive-monotone at task-level), the
   **advisory** live-probe guidance (process-start fine at plan-level; steer away from
   network/poll/daemon at both — the catalogue steers, it does NOT emit a harness-enforced rule),
   and the volume-control gate; teach `guardrails-review` to **WARN** (not block) on a live-probe check.
   **This migration is SSOT-first
   and GATED ON THE HARNESS BUILD:** the golden round-trip stays **RED until the loader understands the
   four folders**, so step 3 lands *after* (or interleaved-behind) step 2's loader/validator, not
   before. **#181 is REFRAMED, not replaced:** the brownfield green-test baseline becomes a
   `<plan>/preflights/` **positive** check (the general positive-baseline/preflight archetype #181 named)
   — the intent survives, the carrier moves from a no-op-root preflight to the plan-level folder.
   **The #174/#182 no-op-deadlock short-circuit doctrine is REMOVED from this feature's baseline
   context:** with the no-op ROOT/END tasks eliminated, there is no no-op task left to short-circuit, so
   the short-circuit has no action to fire against in the plan-level phases (it remains a general §7
   harness rule for any *real* task that no-ops, untouched — it simply no longer participates in the
   preflight story). Re-author the worked example to the model (§"What the example re-author needs").
   `filesTouched`: `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`,
   `docs/plans/09-preflight-first-class/example/**`.
4. **`guardrails-test-author`** — phase tests, **each run in serial AND worktree mode**: plan-level
   preflight red → halt before any task runs, exit 2, zero tokens; **plan-level preflight passes, then a
   mid-DAG crash + resume → the pre-DAG phase is SKIPPED (the negative baseline is NOT re-evaluated,
   no false-halt)** (B1); plan-level guardrail red → terminal halt, work durable, exit 2; **DAG all-green
   + terminal red + resume → terminal-only resume re-fires only the terminal phase** (B2);
   **`--revalidate-task plan:guardrails` after a hand-fix → green settle, exit 0** (B2); a plan with a
   `<plan>/guardrails/` carrying only a tautological `exit 0` file → **validation FAILS (GR2018
   re-homed)** (B3); task-level preflight red → `task-preflight-failed`/`needs-human` for the cone with
   **no attempt burned**, independent branches keep running; the advisory live-probe-guidance test
   (a plan-level `dotnet test` draws NO warning; a network probe makes `guardrails-review` emit a
   **WARN, not a BLOCKER** — and the harness `validate`/`run` neither warns nor blocks either way,
   because the guidance is advisory, not harness-enforced); the §10 renderer golden
   (container structure, invisible anchors, no `done_` node, anchor→anchor edges, `source-sha256`
   stability) **plus the required staleness test — edit a `<plan>/guardrails/` check → `graph --check`
   reports stale (exit 2)**. `filesTouched`: `tests/**`.

Sequencing rule: the architect's draft-PR review (step 1, including the **placement** resolution)
completes before any harness work (step 2) starts (#106); the harness loader (step 2) precedes the
skill migration (step 3), because the golden round-trip stays red until the loader understands the four
folders.

---

## What the `example/` re-author + diagram renderer will need (for the lead's later step)

**Not done in this pass** (the brief reserves the `example/` re-author for the lead after the design
settles). Specced precisely so the later step is mechanical:

**The `example/` re-author (skill-author, after design sign-off):**
- **Add `<plan>/preflights/`** (plan-root, per open-question #1's recommendation) with **two checks**:
  one **positive** baseline (e.g. `01-all-repo-tests-green` — the existing suite passes on the starting
  repo) and one **negative** assert-absent baseline (e.g. `02-correlation-id-absent` — the artifact the
  plan introduces is not present yet), each a deterministic byte/exit check following the advisory
  live-probe guidance.
- **Add `<plan>/guardrails/`** (plan-root) holding the terminal whole-repo checks (build + full suite +
  any union invariant), replacing the example's former `integrationGate: true` task. **Remove that
  no-op END task** from `tasks/`. The folder MUST carry **≥1 check that actually re-runs the
  integration set** (the re-homed GR2018 content teeth — a tautological `exit 0` file would fail
  validation).
- **Recast the former three "preflight" tasks**: the global pre-DAG positive baseline (the **#181
  brownfield green-test baseline, REFRAMED** as the general positive-preflight archetype) → a
  `<plan>/preflights/` positive check; the assert-absent → a `<plan>/preflights/` negative check; the
  single per-task dependency-delivery illustration → one consuming task's `tasks/<id>/preflights/`
  folder, keyed to a `dependsOn` edge.
- **Do NOT reintroduce a no-op ROOT/END task or its #174/#182 short-circuit scaffolding** — the
  eliminated no-op tasks are exactly what the model removes; there is no no-op action left in the
  example for the short-circuit to fire against.
- **Remove every simulated `scope:"precondition"` marker** — no third scope value exists under this
  model (the partition's BLOCKER (f) collision is dissolved: plan-level checks are folders, not a scope
  value).
- **Re-run `guardrails validate`** on the re-authored folder (it must validate clean once the harness
  understands the new folders — so this step follows the harness build, or uses a hand-checked folder
  the validator will later accept).
- **Update plan-09's prose pointer** to the example once it is re-authored (this pass leaves the prose
  pointer describing the example as pending re-author).

**The diagram renderer (harness-developer, SSOT §10 rewrite):**
- **Prototype against the bundled Mermaid version FIRST** — the container→container edge needs
  invisible anchor nodes and the technique is version-sensitive (below).
- Replace the flat-node + fan-out + `done_<task>` model with **container subgraphs**: one
  `subgraph task_<id>` per task holding nested `Preflights`/`Guardrails` subgraphs of check nodes; two
  plan-level subgraphs (`Full Flight Checks` top, `Terminal Gate` bottom).
- **Edges carried by invisible anchor nodes** — one `<id>_anchor[" "]:::invisible` per container;
  edges drawn **anchor→anchor** (`task_A_anchor --> task_B_anchor` for each `dependsOn`; plan-preflights
  anchor → root-task anchors; leaf-task anchors → plan-guardrails anchor). A `subgraph --> subgraph`
  edge does **not** render faithfully; the anchor is the reliable technique (softens the "no nodes"
  promise to "no *visible* free nodes"). A **task-level preflight still renders its gated `dependsOn`
  edge** (do not erase it). Delete `task --> guardrail` fan-out edges and all `done_<id>` nodes.
- **Five `classDef`s** (task container / preflight check / guardrail check / plan-level container /
  `invisible` anchor).
- **Fold the new structure into `source-sha256`** (container membership + nested check labels +
  container→container edges; stable across ordinal reorderings) — **INCLUDING the plan-level folder
  checks**, which are not in `tasks{}`; else editing a terminal-gate check leaves the diagram falsely
  fresh. So `--check` staleness still fires.
- **Golden test**: assert no `done_` node, container subgraphs present, invisible anchors present,
  anchor→anchor edges, byte-identical re-render on unchanged input.
- **Required staleness test**: edit a `<plan>/guardrails/` check → `graph --check` reports **stale
  (exit 2)**.

---

## Proposed plan-document edits

This document is itself the plan-of-record (`docs/plans/09-preflight-first-class.md`). Companion edits
are **proposed, not yet applied** (the lead approves, then applies):

1. **`docs/plans/02-schemas-and-contracts.md`** — **NO edit now.** The forward edit set lands here
   **only** in the change that implements the model (invariant 4), pre-scoped as:
   - **§1 layout** — `<plan>/preflights/` + `<plan>/guardrails/` (plan-root, pending open-question #1) +
     `tasks/<id>/preflights/`.
   - **§3.3 `integrationGate` migration** — RETIRE GR2017 + the `integrationGate` task kind; **RE-HOME
     GR2018** as "a multi-leaf/fan-in plan MUST have a `<plan>/guardrails/` carrying ≥1 deterministic
     check that actually re-runs the integration set" (NOT "non-empty folder"); **KEEP
     `scope:"integration"`** as the per-union tag (§4.3 unchanged).
   - **§7 journal** — the three new outcomes (`plan-preflight-failed` / `task-preflight-failed` /
     `plan-guardrail-failed`); the additive top-level `planPreflights` + `planGuardrails` sections
     (with `planHash` marker keys); the two new resume rules (**SKIP pre-DAG on a matching
     `planPreflights` marker**; **terminal-only resume** when the DAG is green and the terminal gate is
     red).
   - **§7.1** — `--revalidate-task plan:guardrails` (the synthetic reserved id) + the exit-2 narrative
     for all three halts.
   - **§10 renderer** — container subgraphs + **invisible anchor nodes** carrying anchor→anchor edges;
     `source-sha256` **folds the plan-level folder checks** (not just `tasks{}`); remove fan-out +
     `done_`.
   Recorded here so the future change is pre-scoped.
2. **`docs/plans/03-roadmap.md`** — optionally add a one-line "deferred designs" pointer: *"Preflights
   & guardrails first-class at two scopes — DEFERRED design of record in `09-preflight-first-class.md`
   (#183). Run-level checks become first-class `<plan>/preflights/` ('Full Flight Checks') +
   `<plan>/guardrails/` (terminal gate) folders bracketing the DAG (replacing the no-op root/end task
   mechanism); per-task `tasks/<id>/preflights/` carries the JIT dependency-delivery check; the diagram
   renders task containers with nested preflight/guardrail sections and container→container edges."*
   (Proposed.)
3. **`docs/plans/README.md`** (the plan index) — keep the `09-preflight-first-class.md` entry as a
   **DEFERRED design**, with the updated one-line summary (two-scope model). (Proposed.)
4. **`docs/plans/09-preflight-first-class/example/`** — re-authored to the two-scope model in a
   **separate reviewed step** (NOT this pass), per §"What the example re-author needs". (Pointer only;
   the example itself is unchanged by this pass.)

No skill or SSOT edit is made by this document — it records the target two-scope model, the re-homed
partition, the diagram-rendering change, the harness phases/outcomes, and the open questions held for
the team and owner.
