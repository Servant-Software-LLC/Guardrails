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

| Scope | Preflight folder | Guardrail folder | Runs | Against |
|---|---|---|---|---|
| **PLAN-LEVEL** (run-level, once) | `<plan>/preflights/` — the **"Full Flight Checks"** | `<plan>/guardrails/` — the **terminal / integration gate** | **once**: preflights BEFORE the DAG, guardrails AT THE END | the starting repo (preflights) / the merged result (guardrails) |
| **TASK-LEVEL** (per task) | `tasks/<id>/preflights/` — JIT dependency-delivery | `tasks/<id>/guardrails/` — postconditions | **per task**, bracketing the action | the task's segment worktree at `taskBase` |

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
| **`<plan>/preflights/`** — "Full Flight Checks": a first-class run-level preflight folder, evaluated once before the DAG against the starting repo; failure halts the run before it starts | **plan-level** | **harness + schema** — a new pre-DAG phase + a new outcome/exit branch; lands in the SSOT only when implemented (invariant 4) |
| **`<plan>/guardrails/`** — the terminal/integration gate: a first-class run-level guardrail folder, evaluated once at the end on the merged HEAD | **plan-level** | **harness + schema** — the terminal phase + the migration from the `integrationGate` *task kind* (§3.3); SSOT change at implementation time |
| **`tasks/<id>/preflights/`** — per-task JIT dependency-delivery check at `taskBase` before the attempt loop (the PARTITION's Bucket C) | **task-level** | **harness + schema** — `TaskExecutor.ExecuteAsync` pre-attempt-loop slot reusing `IReVerifier`; SSOT change at implementation time |
| **`tasks/<id>/guardrails/`** — existing per-task postconditions | **task-level** | **already shipped** — no change |
| The **diagram-rendering change** (container-per-task; plan-level top/bottom containers; remove fan-out + `done_` nodes) | both | **harness (renderer §10)** — `guardrails graph` Mermaid emitter + the `source-sha256` semantic content; SSOT §10 change at implementation time |
| Authoring the checks (which preflight/guardrail goes in which folder, the polarity rules, the volume-control gate) | both | **skill** — `plan-breakdown` catalogue + `guardrails-review` probes |
| The harness **auto-deriving** pre-applicability ("this task modifies, inject a baseline") | — | **out of scope, permanently** — undecidable; false-fails every TDD-red gate |

Everything below the diagram line is **DEFERRED design** — recorded so the future change is
pre-scoped, applied to neither the SSOT nor the code by this document.

---

## Invariants in play

Named, with how the two-scope model bears on each:

1. **Deterministic guardrails over prompt-judges; judges never alone.** *Respected and reinforced.*
   A preflight at either scope is a *deterministic gate run earlier* — the most deterministic possible
   use of a guardrail (no action, no model, just "does the existing thing verify"). The plan-level
   `preflights/` keeps the **live-probe ban**: deterministic, cheap, single-shot byte checks only — NO
   network, NO process start, NO poll (a full-flight-check that halts the whole run must not be a flake
   vector). The REJECTED auto-derivation reading would have introduced a *harness-side judgment*
   ("does this task modify?") that is not deterministic at all — the strongest reason to reject it.

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
   `<plan>/guardrails/`, `tasks/<id>/preflights/`), the new outcomes (`preflight-failed`,
   `integration-gate-failed`), the new pre-DAG/terminal phases, the §10 renderer change, and the
   `integrationGate`-task-kind migration are all **forward design**; each lands in the SSOT **only**
   in the change that implements it.

5. **Honest halts — nothing marked done unverified; needs-human is a feature.** *Respected and
   extended.* A red plan-level preflight is the most honest halt there is: "your starting point is
   already broken; the run will not begin." It halts **before any token is spent** — the cheapest
   possible halt. A red plan-level guardrail halts at the end with the merged result intact on the
   plan branch — the work is durable, the human finishes the merge.

6. **Plain files, light setup — no databases, daemons, or SaaS in v1.** *Respected.* Every scope is
   plain files in the plan folder; the live-probe ban keeps even the plan-level preflight to
   deterministic byte checks (no daemon, no network). The harness reuses the existing integration
   worktree, segment worktrees, and the `IReVerifier` seam — no new infrastructure.

**The decisive invariant pairing is 1 + 5.** The plan-level preflight phase exists *to make the
honest halt cheap* (invariant 5) while staying deterministic and flake-free (invariant 1, via the
live-probe ban). The reason a plan-level preflight is a **folder, not a no-op task**, is the same
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
  outcome (`preflight-failed`) and exit branch (§"Harness phases"); **no task runs, no token is
  spent.** This is the honest, zero-burn halt.
- **Where.** The integration worktree at user HEAD. Read-only — no fragment, no commit.
- **Hard constraint — the live-probe ban.** Deterministic, cheap, single-shot byte checks ONLY: NO
  network, NO process start, NO poll. A plan-level preflight halts the **whole run**, so a flaky probe
  there is the maximal-blast-radius SPOF the ban exists to forbid. "Is my endpoint up?" must be
  expressed as a byte-check on the wired source (`Select-String 'MapGet("/health")'`), never a live
  HTTP call. (A genuinely live check belongs in a task's own guardrail, where a flake costs one task's
  retry budget — not the whole run.)

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
  seam. **On failure → a terminal halt** (`integration-gate-failed` → exit 2): the work is durable on
  the plan branch, the human finishes.
- **Relationship to today's `integrationGate` task kind.** This folder **largely subsumes** the §3.3
  `integrationGate: true` *task*. Today the terminal gate is modelled as a task whose guardrails are
  the `scope:"integration"` set; the owner's model makes it a first-class **folder** instead, removing
  the no-op END task from the DAG and the diagram. **The migration is specced below** (§"Harness
  phases" → "Terminal phase") and flagged as an open question (does the folder fully replace the task
  kind, or coexist for a deprecation window?).

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
  *more* true as merges land — it never union-inverts the way a negative check does); the live-probe
  ban applies here too (deterministic byte check, NO process/poll/network); keyed to a `dependsOn`
  edge the author already drew. **The harness never derives it** — the skill authors it against the
  edge, the human reviews it.

### Task-level guardrails — `tasks/<id>/guardrails/` (existing postconditions)

Unchanged. The per-task acceptance checks run after the action, in filename sort order, all must
pass. This document changes nothing here; it is named only to complete the 2×2.

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

- **Union-inversion (a negative check re-run post-merge false-halts) → DISSOLVED by construction.**
  Negative baselines (Bucket B) are **plan-level one-shots**: they run **once, at the run's start**,
  against the starting repo — never per-task, never at a union, never on merged bytes. A negative
  check evaluated only at the one point in time it is true cannot invert. This is *why* the negative
  baseline is plan-level-only and not a task-level preflight (a task-level negative check at a
  downstream `taskBase` would see earlier merges and false-halt — forbidden).
- **Flaky-SPOF (a plan-level preflight halts the whole run) → now ACCEPTED as correct.** For a
  *full-flight-check*, halting the whole run on failure is the **intended** behavior — the plane does
  not leave the runway on a failed pre-flight. The mitigation is **not** to narrow the blast radius
  (that would defeat the purpose) but to **forbid the flake**: the **live-probe ban** (deterministic /
  cheap / single-shot; no network / process-start / poll) keeps a plan-level preflight from ever being
  a *flaky* SPOF. A deterministic check that genuinely fails *should* halt the run.

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
- **Edges go container→container**, drawn between subgraphs (Mermaid draws a subgraph-to-subgraph edge
  by linking the subgraph ids). For each task B that `dependsOn` A: `task_A --> task_B`. The plan
  preflights subgraph links into every first-level (root) task; every leaf task links into the plan
  guardrails subgraph.
- The fan-out edges (`task --> guardrail`) and the `done_<id>` nodes are **deleted**. Guardrails (and
  preflights) are no longer free nodes with their own dependency edges; they are **contents of the
  task's subgraph**, not participants in the DAG.
- **`classDef`s** color the four kinds (task container, preflight check, guardrail check, plan-level
  container) distinctly; retry/feedback edges remain out of scope.
- **`source-sha256`** (the staleness key) must fold the new structure into its semantic content
  (container membership + nested check labels + container→container edges), so a freshness check still
  fires when any of them changes — and stays stable across irrelevant reorderings (subgraphs and their
  contents sorted ordinal).

This is a **non-trivial renderer rewrite** — the node/edge model changes from "tasks + guardrails +
done-nodes, all free nodes" to "containers with nested checks, edges between containers." Specced
here as a deferred SSOT §10 change; the implementation handoff names it.

---

## Harness phases to spec (still deferred — design only)

Three phases a future implementation would add, plus the `IReVerifier` reuse. **Every item is forward
design, not yet in the SSOT or the code.**

### Pre-DAG phase — evaluate `<plan>/preflights/` once

- **When.** After load/validate, **before the Scheduler builds waves** — the first thing a `run`
  does after it has a valid plan.
- **Where.** The integration worktree on the plan branch at the user's HEAD (the starting bytes),
  read-only. (Open question: interaction with `maxParallelism`/worktree mode — the plan-level phase is
  inherently serial and single-shot, so it likely runs once on the integration worktree regardless of
  `maxParallelism`; recorded as an open question.)
- **Failure → halt before scheduling.** A new outcome `preflight-failed`; **no task runs.** Exit
  code: a plan-level preflight failure is an **actionable, work-not-started** halt → **exit 2** (the
  same class as needs-human: "actionable condition found; nothing was spent / started"). The journal
  records a plan-level `preflights` result distinct from any task. (Open question: whether to mint a
  *new* exit code or reuse exit 2 — recommended: reuse 2, no new code, consistent with the existing
  "actionable condition" semantics.)
- **Auto-derivation rejection holds.** The harness runs the authored `<plan>/preflights/` checks; it
  never infers which checks should be preflights.

### Terminal phase — evaluate `<plan>/guardrails/` once on the merged HEAD

- **When.** After every task has settled green, on the merged plan-branch HEAD — replacing the
  terminal `integrationGate` *task* run.
- **Where.** The integration worktree, via the existing attempt-decoupled `IReVerifier` seam (the same
  seam today's terminal gate and union re-verify use). **No new guardrail-runner machinery.**
- **Migration from the `integrationGate` task kind (§3.3).** Today GR2017 requires a multi-leaf/fan-in
  plan to declare exactly one `integrationGate: true` sink, and GR2018 requires that sink to carry ≥1
  `scope:"integration"` guardrail. Under the model, the terminal checks move into `<plan>/guardrails/`.
  Two migration shapes, flagged as an open question:
  - **(i) Full replacement.** `<plan>/guardrails/` *is* the terminal gate; the `integrationGate` task
    kind and GR2017/GR2018 retire (replaced by "a multi-leaf/fan-in plan MUST have a non-empty
    `<plan>/guardrails/`"). Cleanest; removes the no-op END task entirely.
  - **(ii) Coexistence / deprecation window.** Both are accepted for a release; `plan-breakdown` emits
    the folder, the task kind is deprecated. Safer migration, more surface for a window.
  - **Recommended: (i) full replacement**, because the no-op END task is exactly the clutter the owner
    is removing — but this is a contract retirement (GR2017/GR2018), so it is an OPEN question for the
    owner, not resolved here.
- **Failure → terminal halt** (`integration-gate-failed` → exit 2). The work is durable on the plan
  branch; the merge-collision attribution (#175) carries over (it is a property of the gate failure,
  not of where the gate lives).
- **Relationship to per-union re-verify.** The §4.3 per-union integration-set re-verify is unchanged —
  it runs at every fan-in / non-FF integration *during* the run. `<plan>/guardrails/` is the **final**
  whole-HEAD gate. (Open question: is `<plan>/guardrails/` the same set as the `scope:"integration"`
  union set, or a superset? Likely: the union set is a subset run more often; the plan-level guardrail
  folder is the terminal whole-repo gate. Recorded.)

### Task-level preflight slot — `tasks/<id>/preflights/` (the PARTITION's harness-feasibility note, carried forward)

- **Slot-in point.** `TaskExecutor.ExecuteAsync`, **before the attempt loop** — it gates loop entry.
- **Runner reuse.** Reuses the existing attempt-decoupled `IReVerifier` seam (it already runs a
  guardrail set against arbitrary bytes outside an attempt lifecycle, cwd = a given worktree, no
  `GUARDRAILS_ACTION_*` vars) — here pointed at the consumer's `taskBase`. **No new machinery.**
- **Failure.** Short-circuits to `needs-human` **without consuming a retry attempt** (no-burn), in
  **both** serial and worktree mode (the fail-fast is structural, not budget-dependent). Outcome
  `preflight-failed` (shared with the plan-level phase, distinguished by scope in the journal). Blocks
  only the task + its transitive dependents via the existing scheduler closure; independent branches
  keep running.
- **Effort: S–M** for the task-level slot; **M** for the plan-level phases + the §10 renderer rewrite.

### New outcomes / exit codes / journal entries (summary)

- **`preflight-failed`** — a plan-level OR task-level preflight failed. Plan-level → halt before
  scheduling; task-level → `needs-human` for that task's cone, no attempt burned. → **exit 2**.
- **`integration-gate-failed`** (or reuse `guardrail-failed` at terminal scope — open question) — the
  plan-level `<plan>/guardrails/` terminal gate failed on the merged HEAD → terminal halt, work durable
  on the plan branch → **exit 2**.
- **Journal.** Plan-level preflight and plan-level guardrail results are recorded **outside the
  per-task `tasks{}` map** (they are not tasks) — a new `preflights` / `guardrails` section in
  `run.json`. (Open question: exact journal shape; recorded.)
- **No new exit code recommended** — both new halts are the existing exit-2 "actionable condition;
  work durable/unstarted" class. Confirm at implementation time.

---

## Open questions (flagged for the team + owner — NOT resolved here)

1. **Exact on-disk placement of the plan-level folders.** The owner pointed at the `…/texttools/tasks`
   level. Two options:
   - **(A) Plan-root siblings of `tasks/`** — `<plan>/preflights/` and `<plan>/guardrails/` sit
     alongside `guardrails.json`, `state/`, `tasks/`. **RECOMMENDED:** the model's whole point is that
     these are **plan-level**, not task-level; placing them at the plan root makes the scope visible on
     disk (plan-level folders at the plan root; task-level folders under each task). It also keeps the
     `tasks/` directory a pure list of tasks.
   - **(B) Inside `tasks/`** — e.g. `tasks/preflights/`, `tasks/guardrails/`. Closer to where the owner
     pointed, but it mixes a plan-level concern into the per-task directory and risks colliding with a
     task whose folder is literally named `preflights`/`guardrails`.
   - **Recommendation: (A) plan-root.** Marked **OPEN** for the owner.
2. **Does `<plan>/guardrails/` fully REPLACE the `integrationGate` task kind, or coexist?** Full
   replacement (retire GR2017/GR2018, replace with a "non-empty `<plan>/guardrails/`" rule) is
   recommended and cleanest; coexistence is a safer migration window. **OPEN** (a contract retirement —
   owner sign-off).
3. **Terminology — keep "Full Flight Checks"** (the owner's term) as the user-facing name for
   plan-level preflights? The on-disk folder is `preflights/`; "Full Flight Checks" is the diagram /
   UI label. Recommend keeping the owner's term as the display name. **OPEN.**
4. **Interaction with `maxParallelism` / worktree mode for the plan-level phases.** The plan-level
   pre-DAG and terminal phases are inherently **serial, single-shot** (one evaluation on the
   integration worktree). Recommend they run once regardless of `maxParallelism`, on the integration
   worktree, never sharded across segment worktrees. Confirm the serial-mode (`maxParallelism: 1`)
   behavior is identical. **OPEN.**
5. **Is `<plan>/guardrails/` the same set as the `scope:"integration"` union set, or a superset?**
   (How the terminal folder relates to the per-union re-verify — §4.3.) **OPEN.**
6. **Journal shape for plan-level results** (the new `preflights`/`guardrails` sections in `run.json`)
   and whether `integration-gate-failed` is a distinct outcome or a terminal-scoped `guardrail-failed`.
   **OPEN.**

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
  flight-check + forbidden-from-flaking by the live-probe ban. So this is a *scoped* reinstatement with
  the failure modes addressed, not an unconditioned walk-back.

- **Counter: "The live-probe ban guts the most compelling preflight — 'is my dependency's endpoint
  *up*?' — the literal 'plane on the runway' intuition #183 opens with."** *Response:* Correct, and an
  honest tension. But a live endpoint probe in a *plan-level* preflight is the maximal-blast-radius
  flake — it halts the whole run on a network hiccup. The intuition is preserved as a **byte-check on
  the wired source** (the route is `MapGet`-registered in the committed file), which is deterministic,
  single-shot, and fully expressible. A genuinely *live* check belongs in a task's own guardrail, where
  a flake costs one task's retry budget, not the run. The ban is a conservative default, not a new veto.

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
only if the owner approves the model and resolves the open questions (especially #1 placement and #2
`integrationGate` migration). Sequencing is gated on the design-of-record draft-PR review (#106): this
doc lands as a draft PR for inline human review **before** any milestone starts.

1. **Architect (this agent)** — once the open questions are resolved, deliver the active design + the
   verbatim SSOT edit set as a **draft PR for inline review** (#106). `filesTouched`:
   `docs/plans/09-preflight-first-class.md` (promote DEFERRED → active) +
   `docs/plans/02-schemas-and-contracts.md` (the edit set: §1 layout, §3.3 migration, §7 outcomes,
   §7.1 exit, §10 renderer).
2. **`guardrails-harness-developer`** — the three phases + the §10 renderer. Sequencing: SSOT edit +
   folder parsing/validation first; then the pre-DAG phase (`<plan>/preflights/` evaluation +
   `preflight-failed` + exit-2 branch); then the terminal phase (`<plan>/guardrails/` on merged HEAD,
   `IReVerifier` reuse, `integrationGate`-task migration); then the task-level preflight slot
   (`TaskExecutor.ExecuteAsync` pre-loop, `IReVerifier` at `taskBase`); then the §10 renderer rewrite
   (container subgraphs, container→container edges, remove fan-out + `done_`, fold structure into
   `source-sha256`). `filesTouched`: `src/Guardrails.Core/Loading/**`, `src/Guardrails.Core/Execution/**`,
   `src/Guardrails.Core/Model/**`, the graph renderer under `src/Guardrails.Core/**`,
   `src/Guardrails.Cli/**`, `docs/plans/02-schemas-and-contracts.md` (same change), `tests/**`.
3. **`guardrails-skill-author`** — teach `plan-breakdown` to emit the four folders (plan-level
   preflights/guardrails; task-level preflights for the dependency-delivery case keyed to a `dependsOn`
   edge), the polarity rules (positive/negative at plan-level; positive-monotone at task-level), the
   live-probe ban, and the volume-control gate; teach `guardrails-review` to probe them. Re-author the
   worked example to the model (§"What the example re-author needs"). `filesTouched`:
   `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`,
   `docs/plans/09-preflight-first-class/example/**`.
4. **`guardrails-test-author`** — phase tests: plan-level preflight red → halt before any task runs,
   exit 2, zero tokens; plan-level guardrail red → terminal halt, work durable, exit 2; task-level
   preflight red → `needs-human` for the cone with **no attempt burned**, independent branches keep
   running, in BOTH modes; the live-probe-ban rejection test; the §10 renderer golden (container
   structure, no `done_` node, container→container edges, `source-sha256` stability). `filesTouched`:
   `tests/**`.

Sequencing rule: the architect's draft-PR review (step 1, including open-question resolution) completes
before any harness work (step 2) starts (#106).

---

## What the `example/` re-author + diagram renderer will need (for the lead's later step)

**Not done in this pass** (the brief reserves the `example/` re-author for the lead after the design
settles). Specced precisely so the later step is mechanical:

**The `example/` re-author (skill-author, after design sign-off):**
- **Add `<plan>/preflights/`** (plan-root, per open-question #1's recommendation) with **two checks**:
  one **positive** baseline (e.g. `01-all-repo-tests-green` — the existing suite passes on the starting
  repo) and one **negative** assert-absent baseline (e.g. `02-correlation-id-absent` — the artifact the
  plan introduces is not present yet), each a deterministic byte/exit check honoring the live-probe ban.
- **Add `<plan>/guardrails/`** (plan-root) holding the terminal whole-repo checks (build + full suite +
  any union invariant), replacing the example's former `integrationGate: true` task. **Remove that
  no-op END task** from `tasks/`.
- **Recast the former three "preflight" tasks**: the global pre-DAG positive baseline → a
  `<plan>/preflights/` positive check; the assert-absent → a `<plan>/preflights/` negative check; the
  single per-task dependency-delivery illustration → one consuming task's `tasks/<id>/preflights/`
  folder, keyed to a `dependsOn` edge.
- **Remove every simulated `scope:"precondition"` marker** — no third scope value exists under this
  model (the partition's BLOCKER (f) collision is dissolved: plan-level checks are folders, not a scope
  value).
- **Re-run `guardrails validate`** on the re-authored folder (it must validate clean once the harness
  understands the new folders — so this step follows the harness build, or uses a hand-checked folder
  the validator will later accept).
- **Update plan-09's prose pointer** to the example once it is re-authored (this pass leaves the prose
  pointer describing the example as pending re-author).

**The diagram renderer (harness-developer, SSOT §10 rewrite):**
- Replace the flat-node + fan-out + `done_<task>` model with **container subgraphs**: one
  `subgraph task_<id>` per task holding nested `Preflights`/`Guardrails` subgraphs of check nodes; two
  plan-level subgraphs (`Full Flight Checks` top, `Terminal Gate` bottom).
- **Edges container→container only** (`task_A --> task_B` for each `dependsOn`; plan-preflights → roots;
  leaves → plan-guardrails). Delete `task --> guardrail` fan-out edges and all `done_<id>` nodes.
- **Four `classDef`s** (task container / preflight check / guardrail check / plan-level container).
- **Fold the new structure into `source-sha256`** (container membership + nested check labels +
  container→container edges; stable across ordinal reorderings) so `--check` staleness still fires.
- **Golden test**: assert no `done_` node, container subgraphs present, container→container edges,
  byte-identical re-render on unchanged input.

---

## Proposed plan-document edits

This document is itself the plan-of-record (`docs/plans/09-preflight-first-class.md`). Companion edits
are **proposed, not yet applied** (the lead approves, then applies):

1. **`docs/plans/02-schemas-and-contracts.md`** — **NO edit now.** The forward edit set (§1 layout for
   `<plan>/preflights/` + `<plan>/guardrails/` + `tasks/<id>/preflights/`; §3.3 `integrationGate`
   migration; §7 `preflight-failed` / `integration-gate-failed` outcomes; §7.1 exit-2 narrative; §10
   container-renderer rewrite + `source-sha256` semantics) lands here **only** in the change that
   implements the model (invariant 4). Recorded here so the future change is pre-scoped.
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
