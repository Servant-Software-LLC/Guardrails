# Plan: Preflights & guardrails, first-class at two scopes (implementation)

> **This is the "WHAT to build" brief** for the two-scope preflights/guardrails feature — the
> reviewed input a `/plan-breakdown` turns into a Guardrails task DAG that the `guardrails` CLI runs
> to BUILD the feature (we are **dogfooding** it: Guardrails builds Guardrails). It is distilled from
> the settled, PLANNED design-of-record **`docs/plans/09-preflight-first-class.md`** (tip `513cd5d`),
> which remains the source of truth for every WHY/detail — consult it for rationale. This brief is
> self-sufficient on the WHAT: numbered deliverables, each with acceptance criteria a deterministic
> guardrail (build/test/validate) can check, grouped so dependency order is obvious.
>
> **Design is settled.** All load-bearing decisions (two scopes × two kinds = four folders; plan-level
> folder placement = **plan root**; hardening B1/B2/B3; advisory live-probe guidance; the F9 outcome
> split; serial-mode `IReVerifier` wiring; the container-diagram rewrite) are CLOSED in plan-09
> "Open questions." This plan does **not** relitigate them.

---

## The feature in one paragraph

**Preflights and guardrails are each first-class at two scopes** — PLAN-LEVEL (run once, bracketing the
whole run) and TASK-LEVEL (run per task, bracketing each task's action) — and the folder layout mirrors
exactly that. Four folders:

| Scope | Preflight folder | Guardrail folder | Runs | Against |
|---|---|---|---|---|
| **PLAN-LEVEL** (once) | `<plan>/preflights/` — **"Full Flight Checks"** | `<plan>/guardrails/` — **"Terminal Gate"** | preflights BEFORE the DAG, guardrails AT THE END | starting repo (preflights) / merged HEAD (guardrails) |
| **TASK-LEVEL** (per task) | `tasks/<id>/preflights/` — JIT dependency-delivery | `tasks/<id>/guardrails/` — postconditions (**exists today, unchanged**) | per task, bracketing the action | the task's segment worktree at `taskBase` |

The plan-level folders are **siblings of `tasks/`, `guardrails.json`, and `state/`** at the plan root.
This model **replaces** the prior no-op-ROOT-task baseline and no-op-END `integrationGate`-task
mechanisms with first-class folders the harness evaluates in dedicated phases, and rewrites the
diagram renderer so each task is a self-contained container.

## Invariants in play (name them; the design respects each)

- **(4) SSOT is the single source of truth — a contract change lands in `02-schemas-and-contracts.md`
  in the SAME change that motivates it.** Deliverable 8 is not optional polish: the harness change and
  its SSOT edit are ONE change. plan-09 itself makes no SSOT edit; this build does.
- **(2) Harness is the single writer of merged state; children get snapshots, write fragments.** Every
  phase is read-only against its bytes (plan-preflights at user HEAD; plan-guardrails on merged HEAD via
  `IReVerifier`; task-preflights in the consumer's segment worktree BEFORE the action). No phase writes
  a fragment or a commit.
- **(3) Prompt-guardrail verdicts come from verdict files, never CLI exit codes.** Preflights and
  plan-guardrails are ordinary guardrails evaluated at a new phase — no new verdict source.
- **(1) Deterministic guardrails over prompt-judges; judges never alone** and **(5) honest halts.** A
  red preflight is the cheapest honest halt (zero tokens spent). The live-probe guidance (deliverable 9)
  keeps a plan-level halt flake-free — but it is ADVISORY (skill steers, review WARNs, harness does not
  enforce).

---

## Scope, dependency ordering, and grouping

Deliverables are grouped into four waves by dependency. **The critical ordering constraints** (from
plan-09 "Implementation handoff"):

- **Wave 0 (prerequisite):** Deliverable 1 — wire `IReVerifier` unconditionally. Everything else that
  runs a guardrail set at a phase depends on it. **It comes first.**
- **Wave 1 (harness contract + loader):** Deliverables 2 (loader/validate), 8 (SSOT — lands WITH the
  loader per invariant 4). These are the foundation the phases and skills build on.
- **Wave 2 (harness phases + renderer):** Deliverables 3, 4, 5, 6 (the three phases + outcomes/journal)
  depend on 1 + 2 + 8. Deliverable 7 (renderer) depends on 2's folder model but is otherwise
  **independent of the phases** — it can proceed in parallel with 3/4/5/6.
- **Wave 3 (skills + example, SSOT-first):** Deliverables 9 (skill migration) and 10 (example re-author)
  come **after** the loader (2) — their golden round-trip stays RED until the loader understands the four
  folders. Deliverable 11 (tests) is authored test-first alongside each deliverable it covers, and its
  integration goldens land after 9/10.
- **Wave 3.5 (migration — review-added):** Deliverable 12 migrates the retired-`integrationGate` consumers
  (behavior-pinning tests, regression fixtures, and the `parallel-hello` / `08-parallel-execution` example
  plans) so the suite goes green under the new loader. It `dependsOn` the loader (2); **the terminal gate
  `dependsOn` 12**. This plan's OWN terminal gate stays `integrationGate: true` (bootstrap exemption).

**Test-first where it fits:** the skill defaults to TDD, so most code deliverables split into an
author-tests task (red) then an implement task (green). Deliverable 11 enumerates the specific
review-named tests each phase must carry; those tests are authored BEFORE the phase they gate.

```
Wave 0:  [1] IReVerifier unconditional
              │
Wave 1:  [2] loader + validate (4 folders, GR2027+, GR2017/GR2018 migration) ── [8] SSOT edits (same change)
              │
Wave 2:  [3] pre-DAG phase      [4] terminal phase      [5] task-preflight slot      [6] outcomes+journal
              └──────────────── all depend on 1+2+8 ────────────────┘        [7] diagram renderer (parallel; needs 2)
              │
Wave 3:  [9] skill migration ── [10] example re-author ── [11] tests (test-first per deliverable; goldens after 9/10)
              │
Wave 3.5:[12] migrate retired-integrationGate consumers (tests + example plans; needs 2) ── heals the suite
              │                    the terminal gate depends on [12]; this plan's OWN gate is a bootstrap exemption
```

> **From /guardrails-review (folded in below):** deliverable **12** (migrate the retired-`integrationGate`
> consumers) is a review-added wave; the **Guardrail-strength requirements** section lists the per-deliverable
> guardrail hardening the regenerated DAG must carry.

---

# Deliverables

## Wave 0 — Prerequisite

### 1. Wire `IReVerifier` unconditionally in `SchedulerFactory`

**What.** Today the attempt-decoupled `IReVerifier` seam (the runner that evaluates a guardrail set
against arbitrary bytes outside an attempt lifecycle) is constructed **only** in worktree mode
(`maxParallelism > 1 && git`), because its only current caller (the §4.3 per-union re-verify) never
fires in serial mode. The pre-DAG, terminal, and task-level preflight phases all reuse this seam — so
it must be constructed in **both** serial and worktree mode. Change the `SchedulerFactory` composition
root to wire `IReVerifier` (or an equivalent attempt-decoupled guardrail runner) **unconditionally**.

**Why it is first.** If the phases reuse a runner that is null at `maxParallelism: 1`, they **silently
no-op in serial mode** — a hidden false-green: the honest halt the phase exists to produce never fires.
This is where a false-green could hide, so every phase test (deliverable 11) runs in serial AND worktree
mode.

**Acceptance criteria (deterministic):**
- A unit/integration test constructs the scheduler with `maxParallelism: 1` and asserts the
  `IReVerifier` (or its equivalent) is **non-null** (a `Factory_Wires*`-style composition-root assertion,
  not a seam-injecting test).
- The same assertion holds at `maxParallelism > 1` (no regression).
- Build + existing suite green.

**Files (for the breakdown's guidance, not authoritative):** `src/Guardrails.Core/**` (the
`SchedulerFactory` composition root), `tests/**`.

---

## Wave 1 — Harness contract + loader

### 2. Loader + validation for the four folders (with GR2027+ and the GR2017/GR2018 migration)

**What.** Extend the plan-folder loader/validator to parse and validate all four folders:
- **Plan-level:** `<plan>/preflights/` and `<plan>/guardrails/` at the **plan root** (siblings of
  `tasks/`, `guardrails.json`, `state/`).
- **Task-level:** `tasks/<id>/preflights/` (sibling of the existing `tasks/<id>/guardrails/`).

Each folder holds deterministic-first guardrail files with the same file grammar as today's
`tasks/<id>/guardrails/` (`NN-name.ps1`/`.sh`/`.py` + optional `.json` sidecar, or `NN-name.prompt.md`
with YAML frontmatter; `catches:` comment required; ordinal filename sort). Reuse the existing guardrail
file parser — the folders differ only in **where** they live and **when** they run, not in file shape.

**New diagnostics (GR2027+, next-free code — GR2026 is the last taken; confirm next-free at
implementation time and record it):** malformed declarations in any of the four folders — e.g. an empty
plan-level folder that the plan requires to be non-empty, a guardrail file missing its `catches:`
comment, an unresolvable interpreter, a malformed sidecar. Allocate contiguous codes and document each
in the SSOT (deliverable 8).

**RETIRE GR2017 + the `integrationGate` task kind; RE-HOME GR2018 onto the folder (B3).** Today:
- **GR2017** requires a multi-leaf/fan-in plan to declare exactly one `integrationGate: true` sink.
- **GR2018** requires that sink to carry ≥1 `scope: "integration"` guardrail ("a gate with none
  verifies nothing").

Under the model the terminal checks move into `<plan>/guardrails/`. So:
- **Retire GR2017 and the `integrationGate: true` task kind entirely** — no coexistence window. A plan
  no longer declares a terminal sink task. A plan that STILL declares `integrationGate: true` gets a
  **hard validation error** (a new GR2027+ unsupported-key code) — RESOLVED (note 3), honest-over-silent.
  Every existing committed consumer of the retired behavior is migrated by **deliverable 12**.
- **Re-home GR2018's CONTENT teeth onto the folder — do NOT weaken it to "non-empty folder."** The
  replacement rule (a **new GR code**, allocated in the GR2027+ block): *a multi-leaf or fan-in plan MUST
  have a non-empty `<plan>/guardrails/` folder carrying **≥1 deterministic check that actually re-runs the
  integration set** (the whole-repo build / full suite / a union invariant) — not merely a present file.*
  A tautological `exit 0` file must FAIL validation. (Implementation detail for the SSOT: the check may
  reuse the §4.3 `scope:"integration"` tag as the "counts toward the terminal gate" marker on the folder's
  files, or a folder-scoped equivalent; either way the ≥1-real-integration-set-re-run obligation survives.)
- **KEEP `scope: "integration"` as the per-union tag.** The §4.3 per-union re-verify is UNCHANGED — it
  still runs the integration-scoped set at every union during the run. Only the *terminal-sink*
  obligation moved to the folder. The terminal folder is **terminal-only**; it is NOT the same object as
  the per-union set (which runs more often).

**Acceptance criteria (deterministic):**
- A fixture plan with all four folders **validates clean** (`guardrails validate` exit 0).
- A malformed declaration in each of the four folders yields the expected GR2027+ code (one assertion
  per new code).
- A multi-leaf/fan-in fixture whose `<plan>/guardrails/` folder is **empty** FAILS validation (the
  re-homed GR2018 rule).
- A multi-leaf/fan-in fixture whose `<plan>/guardrails/` folder contains **only a tautological `exit 0`
  file** FAILS validation (content teeth preserved, not "non-empty").
- A plan still declaring the old `integrationGate: true` task kind → a **hard validation error** (a new
  GR2027+ code), no coexistence window (RESOLVED — note 3). The existing committed consumers are migrated
  by **deliverable 12**.
- The existing `scope: "integration"` per-union tag still parses and drives the §4.3 set (no regression
  in a union-forming fixture).
- Build + the FourFolder tests green. **NOT the whole suite** — retiring GR2017/GR2018 deliberately reddens
  the existing gate tests (`ParallelValidationGateTests` etc.); **deliverable 12 heals them**, and the
  terminal gate (which `dependsOn` deliverable 12) is the whole-suite check.

**Files:** `src/Guardrails.Core/Loading/**`, `src/Guardrails.Core/Model/**`, `tests/**`, and — in the
SAME change — `docs/plans/02-schemas-and-contracts.md` (deliverable 8).

### 8. SSOT update (`02-schemas-and-contracts.md`) — lands WITH the harness change (invariant 4)

**What.** The implementing change edits the SSOT so code and contract never disagree. **This is not a
separate later deliverable — it lands in the same change(s) that implement 2/3/4/5/6/7.** Listed here as
its own numbered item so the breakdown can gate on it, but sequence it INSIDE the harness waves. The
required edits (from plan-09 "Proposed plan-document edits" §1):
- **§1 layout** — add `<plan>/preflights/`, `<plan>/guardrails/` (plan-root), and `tasks/<id>/preflights/`
  to the plan-folder tree.
- **§3.3 `integrationGate` migration** — retire GR2017 + the `integrationGate` task kind; re-home GR2018
  as the "≥1 real integration-set re-run in `<plan>/guardrails/`" folder rule; keep `scope:"integration"`
  as the per-union tag (§4.3 unchanged).
- **§7 journal** — the three new outcomes (`plan-preflight-failed` / `task-preflight-failed` /
  `plan-guardrail-failed`); the two additive top-level sections `planPreflights` + `planGuardrails`
  (each `planHash`-keyed); the two new resume rules (SKIP pre-DAG on a matching `planPreflights` marker;
  terminal-only resume when the DAG is green and the terminal gate is red).
- **§7.1** — `--revalidate-task plan:guardrails` (the synthetic reserved id) and the exit-2 narrative for
  all three halts.
- **§10 renderer** — container subgraphs + invisible anchor nodes carrying anchor→anchor edges;
  `source-sha256` folds the plan-level folder checks (not just `tasks{}`); remove the fan-out and
  `done_<id>` nodes.
- **New GR codes** — document GR2027+ (the four-folder malformed-declaration codes and the re-homed
  GR2018 replacement) in the GR-code table.

**Acceptance criteria:**
- Every contract fact a harness deliverable implements has a matching SSOT paragraph in the SAME diff (a
  reviewer can point at the code and the doc together).
- The SSOT drift-tests (if any guard the promptRunners/schemas mirrors) stay green.
- `guardrails-domain-knowledge` / `guardrails-dev-knowledge` skill self-update rules honored if a fact
  they carry moved.

**Files:** `docs/plans/02-schemas-and-contracts.md` (and any drift-mirrored `references/schemas.md`).

---

## Wave 2 — Harness phases, outcomes, renderer

### 3. Pre-DAG phase — evaluate `<plan>/preflights/` once, with the B1 resume marker

**What.** After load/validate and **before the Scheduler builds waves**, evaluate `<plan>/preflights/`
**once** against the starting repo — the integration worktree on the plan branch at the user's HEAD
(serial mode: the plan workspace), read-only. Runs once regardless of `maxParallelism` (inherently serial
and single-shot; never sharded across segment worktrees).

- **On PASS:** record the **B1 marker** — a new top-level `planPreflights` section in `state/run.json`
  (OUTSIDE `tasks{}`): `{ "status": "passed", "planHash": "sha256:…", "evaluatedAt": "…", "checks": [ … ] }`.
  `planHash` is the SAME `PlanHash` the journal and §13 review marker already use.
- **On FAIL:** halt **before scheduling** — no task runs, zero tokens spent. Outcome
  **`plan-preflight-failed`**, journaled in `planPreflights` (`status: "failed"`), **exit 2**.
- **Resume SKIP rule (the load-bearing B1 fix):** the resume pre-pass reads `planPreflights`; if
  `status == "passed"` AND its `planHash` matches the current `PlanHash`, the pre-DAG phase is **SKIPPED**
  — the plan-level preflights are NOT re-run. This makes a NEGATIVE baseline (e.g. "`RequestId` absent")
  evaluated **exactly once** across the whole run lifecycle, so a mid-DAG crash + resume never re-runs it
  against partially-merged bytes and false-halts.
- **Re-run only on `--fresh` or `planHash` mismatch.** `--fresh` clears `run.json` (so the marker clears);
  a `planHash` mismatch (plan changed) forces re-evaluation.

**Acceptance criteria (deterministic; each in serial AND worktree mode):**
- Fixture with a RED `<plan>/preflights/` check → run halts before any task runs; **exit 2**; the journal
  shows zero attempts; `planPreflights.status == "failed"`.
- Fixture with a GREEN `<plan>/preflights/` → `planPreflights.status == "passed"` with a matching
  `planHash`; the DAG then schedules normally.
- **B1 negative-baseline resume test:** a fixture whose `<plan>/preflights/` includes a check true only at
  the start (absence of an artifact a task then introduces); pass it, simulate a mid-DAG interruption,
  then `guardrails run` (resume) → the pre-DAG phase is **SKIPPED** (the negative check is NOT re-run, no
  false-halt), evidenced by the marker read and no re-evaluation.
- `--fresh` after a passed marker re-evaluates the pre-DAG phase.
- Journal round-trips losslessly with the `planPreflights` section present and absent.

**Files:** `src/Guardrails.Core/Execution/**`, `src/Guardrails.Core/Model/**` (journal), `src/Guardrails.Cli/**`, `tests/**`.

### 4. Terminal phase — evaluate `<plan>/guardrails/` once on merged HEAD, with the B2 human-fix path

**What.** After every task settles green, evaluate `<plan>/guardrails/` **once** on the merged
plan-branch HEAD via the existing attempt-decoupled `IReVerifier` seam (the same seam the §4.3 per-union
re-verify and today's terminal gate use — no new runner). This **replaces** the terminal
`integrationGate` task run (Scheduler ~231–253).

- **On FAIL:** terminal halt. Outcome **`plan-guardrail-failed`**, journaled in a new top-level
  `planGuardrails` section (OUTSIDE `tasks{}`): `{ "status": "failed", "planHash": "…", "failedChecks": [ … ] }`,
  **exit 2**. The work is durable on the plan branch; the merge-collision attribution (#175) carries over.
- **B2(a) — revalidate after a hand-fix via a synthetic id (NOT a new verb).** The existing
  `--revalidate-task <id>` must accept the reserved synthetic id **`plan:guardrails`**:
  `guardrails run --revalidate-task plan:guardrails` runs **only** the `<plan>/guardrails/` checks against
  the current merged HEAD (pointing `IReVerifier` at the integration worktree the harness owns). Pass ⇒
  terminal phase settles green, run exits 0; fail ⇒ still `plan-guardrail-failed`, exit 2. (The `:` is
  already disallowed in a real `stableId`/folder id — §3 `^[a-z0-9][a-z0-9._-]*$` — so `plan:guardrails`
  can never collide with a real task. Mint `plan:preflights` too, for re-confirming a hand-fixed starting
  state without `--fresh`.)
- **B2(b) — terminal-only resume.** On a plain `guardrails run` (resume) where **every task is
  `succeeded`** but `planGuardrails.status == "failed"` (and `planHash` matches): all tasks SKIP via the
  existing resume rule (no attempt burned), then the resume pre-pass **re-fires ONLY the terminal phase**
  on the current merged HEAD. A `planHash` mismatch falls back to a normal resume.

**Acceptance criteria (deterministic; each in serial AND worktree mode):**
- Fixture with a RED `<plan>/guardrails/` check → terminal halt after the DAG drains green; **exit 2**;
  `planGuardrails.status == "failed"`; the plan branch still carries all task work (durable).
- **B2(a):** after a red terminal gate, hand-fix the merged HEAD, run `--revalidate-task plan:guardrails`
  → green settle, **exit 0**; a still-failing gate → `plan-guardrail-failed`, exit 2.
- **B2(b):** DAG all-green + terminal red + plain `guardrails run` → all tasks skip (no attempt burned) and
  ONLY the terminal phase re-fires (terminal-only resume).
- Journal round-trips losslessly with `planGuardrails` present and absent.
- The per-union §4.3 re-verify still fires at unions during the run (no regression — the terminal folder
  did not absorb the per-union set).

**Files:** `src/Guardrails.Core/Execution/**` (Scheduler terminal path), `src/Guardrails.Core/Model/**`, `src/Guardrails.Cli/**` (revalidate id), `tests/**`.

### 5. Task-level preflight slot — `tasks/<id>/preflights/`

**What.** In `TaskExecutor.ExecuteAsync`, **BEFORE the attempt loop**, evaluate `tasks/<id>/preflights/`
via `IReVerifier` pointed at the consumer's segment worktree at `taskBase`. It gates loop entry — a JIT
check that the producer this task `dependsOn` actually delivered the type/route/symbol into the bytes this
task inherited, before spending an attempt building against it.

- **On PASS:** attempts proceed normally.
- **On FAIL:** short-circuit to `needs-human` **WITHOUT consuming a retry attempt** (the no-burn property),
  in **BOTH** serial and worktree mode (structural, not budget-dependent). Outcome
  **`task-preflight-failed`** — a **`TaskOutcome` inside `tasks{}`** (it IS a per-task result), distinct
  from the plan-level `plan-preflight-failed`. Blocks **only** this task and its transitive dependents via
  the existing scheduler closure; independent branches keep running.

**Acceptance criteria (deterministic; each in serial AND worktree mode):**
- Fixture where a consumer's `tasks/<id>/preflights/` check is RED (producer's contribution absent) → the
  consumer settles `needs-human` with outcome `task-preflight-failed`, **no attempt burned** (assert the
  attempt count did not increment for the preflight failure).
- The consumer's transitive dependents are `blocked`; an **independent** branch runs to completion (cone
  isolation).
- Exit 2 for the run when a task-preflight blocks a cone.
- A GREEN task-preflight lets the attempt loop proceed (no behavior change vs today).

**Files:** `src/Guardrails.Core/Execution/**` (`TaskExecutor`), `src/Guardrails.Core/Model/**`, `tests/**`.

### 6. Outcomes + journal (the F9 split)

**What.** Add the three new outcomes and the two additive journal sections; all reuse **exit 2**.
- **`plan-preflight-failed`** — pre-DAG phase failed → halt before scheduling → exit 2 → journaled in
  top-level `planPreflights` (outside `tasks{}`).
- **`task-preflight-failed`** — a `tasks/<id>/preflights/` slot failed → `needs-human` for the cone, no
  attempt burned → exit 2 → journaled as a **`TaskOutcome` inside `tasks{}`** (alongside `guardrail-failed`
  / `action-failed` / `output-cap` / `max-turns` / `rate-limited`).
- **`plan-guardrail-failed`** — terminal `<plan>/guardrails/` gate failed on merged HEAD → terminal halt →
  exit 2 → journaled in top-level `planGuardrails` (outside `tasks{}`).

Distinct names (NOT a shared name + a `scope` field): a reader sees `plan-preflight-failed` and knows the
whole run halted; the plan-level scope is also encoded structurally (result lives outside `tasks{}`).

**Journal shape (additive, round-trips losslessly):**
```jsonc
{
  "version": 1, "runId": "…", "planHash": "sha256:…", "nextMergeSequence": 3,
  "planPreflights": { "status": "passed",  "planHash": "sha256:…", "evaluatedAt": "…", "checks": [ … ] },
  "planGuardrails": { "status": "failed",  "planHash": "sha256:…", "failedChecks": [ … ] },
  "tasks": {
    "07-consume-widget": { "status": "needs-human",
      "attempts": [ { "attempt": 1, "outcome": "task-preflight-failed" } ] }
  }
}
```
`planPreflights`/`planGuardrails` are new top-level keys; an older reader ignores them, a plan without the
feature omits them — the existing `tasks{}` shape is untouched.

**Acceptance criteria (deterministic):**
- Each outcome is emitted by its phase and appears in the journal at its correct location (a round-trip
  serialize/deserialize test per outcome).
- All three halts exit **2** (assert the process exit code per phase).
- A journal WITH and WITHOUT the two sections round-trips byte-losslessly (golden round-trip).
- An older-shape journal (no new sections) still loads (backward-compat test).

**Files:** `src/Guardrails.Core/Model/**` (outcome enum + journal), `src/Guardrails.Cli/**` (exit codes), `tests/**`.

### 7. Diagram renderer rewrite (container model) — parallelizable; depends on 2, not on 3/4/5

**What.** Rewrite the `guardrails graph` Mermaid emitter (SSOT §10) to the container model. **Prototype the
Mermaid anchor technique FIRST against the bundled Mermaid version** — it is version-sensitive; a golden
that passes on one version can regress on another. Only after the prototype confirms the technique commit
to the model.

- **Each task = a Mermaid `subgraph task_<id>["<id>"]`** holding two nested subgraphs
  (`<id>_preflights["Preflights"]`, `<id>_guardrails["Guardrails"]`), each containing the individual check
  nodes as small boxes INSIDE the container. **No separate guardrail nodes outside the box.**
- **Two plan-level subgraphs:** `plan_preflights["Full Flight Checks"]` at the TOP,
  `plan_guardrails["Terminal Gate"]` at the BOTTOM.
- **Edges via INVISIBLE ANCHOR NODES** — one `<id>_anchor[" "]:::invisible` per container (a `classDef
  invisible` with no fill/stroke); DAG edges drawn **anchor→anchor** (`task_A_anchor --> task_B_anchor` for
  each `dependsOn`; plan-preflights anchor → root-task anchors; leaf-task anchors → plan-guardrails anchor).
  A `subgraph --> subgraph` edge does NOT render faithfully; the anchor is the reliable technique. (So the
  "no nodes, pure containers" promise softens to "no *visible* free nodes; one invisible anchor per
  container carries the edge.")
- **A task-level preflight STILL renders its gated `dependsOn` edge** — the container→container edge
  remains AND the preflight renders as a check node inside the consumer's Preflights subgraph. (Do NOT
  re-route the edge to originate from the preflight node.)
- **DELETE** the `task --> guardrail` fan-out edges and ALL `done_<id>` "Finished" reconvergence nodes.
- **Five `classDef`s:** task container / preflight check / guardrail check / plan-level container /
  `invisible` anchor.
- **`source-sha256` must fold the new structure** — container membership + nested check labels +
  container→container edges — stable across ordinal reorderings (subgraphs and contents sorted ordinal).
  **CRITICAL: it MUST also fold the PLAN-LEVEL folder checks**, which are not in `tasks{}`; otherwise
  editing a terminal-gate check leaves the diagram falsely fresh (`graph --check` wrongly reports "up to
  date").

**Acceptance criteria (deterministic, golden-based):**
- The renderer prototype demonstrates faithful anchor→anchor rendering against the bundled Mermaid version
  (a checked-in prototype/spike artifact or a rendering assertion) BEFORE the full rewrite is accepted.
- Golden: the emitted Mermaid contains task-container subgraphs with nested `Preflights`/`Guardrails`
  subgraphs, the two plan-level subgraphs, invisible anchors, and anchor→anchor edges.
- Golden ABSENCE assertions: **no `done_` node**, **no `task --> guardrail` fan-out edge**.
- A task-level preflight's `dependsOn` container→container edge is present in the golden.
- Byte-identical re-render on unchanged input (determinism / stable ordering).
- **Required staleness test:** edit a `<plan>/guardrails/` check → `graph --check` reports **stale (exit
  2)** (proves `source-sha256` folds plan-level checks).
- Update ALL existing golden `diagram.*` fixtures + their tests to the new model.

**Files:** the graph renderer under `src/Guardrails.Core/**`, `src/Guardrails.Cli/**` (the `graph`
command + `--check`), the golden `diagram.md`/`diagram.html` fixtures, `tests/**`, `docs/plans/02-schemas-and-contracts.md` §10 (deliverable 8).

---

## Wave 3 — Skills + example (SSOT-first, after the loader)

### 9. Skill migration — `plan-breakdown` + `guardrails-review`

**What.** Teach the skills to author and probe the four-folder model. **This migration is SSOT-first and
GATED ON THE HARNESS LOADER (deliverable 2):** the golden round-trip stays RED until the loader
understands the four folders, so this lands AFTER (or interleaved behind) the loader — never before.

`plan-breakdown` must:
- **Emit the four folders** — plan-level `<plan>/preflights/` (positive AND negative baselines) and
  `<plan>/guardrails/` (terminal whole-repo checks), and task-level `tasks/<id>/preflights/` for the JIT
  dependency-delivery case, keyed to a `dependsOn` edge the author already drew.
- **Catalogue the idioms + polarity rules:** positive/negative at plan-level (negative = one-shot,
  plan-level-only — it cross-references the existing `tests-fail-on-current-code`/`tests-fail-on-stubs`
  anti-tautology archetype, does NOT fork it); positive-monotone-safe at task-level.
- **#181 is REFRAMED, not replaced:** the brownfield green-test baseline becomes a `<plan>/preflights/`
  **positive** check (the general positive-baseline/preflight archetype #181 named). The intent survives;
  the carrier moves from a no-op ROOT task to the plan-level folder.
- **Remove the no-op ROOT/END task scaffolding + the #174/#182 short-circuit dependence from the baseline
  story.** With the no-op tasks eliminated there is no no-op action left in a preflight to short-circuit;
  the #174/#182 short-circuit remains a general §7 rule for any REAL task that no-ops, untouched — it
  simply no longer participates in the preflight story. Remove the `scope:"precondition"` simulated marker
  (no third scope value exists under this model).
- **The re-homed GR2018 authoring rule:** a multi-leaf/fan-in plan's `<plan>/guardrails/` must carry ≥1
  real integration-set re-run (the union invariant / build / suite), not a tautological file.

`guardrails-review` must:
- **Probe the four folders as BLOCKERs** where a required folder/check is missing (e.g. a multi-leaf plan
  with an empty or tautological terminal-gate folder — the re-homed GR2018 obligation).
- **Emit the live-probe guidance as an ADVISORY WARN, not a BLOCK** — see deliverable 9's advisory rule
  below.
- Update `example-breakdown` to the four-folder model.

**Live-probe guidance (ADVISORY — the skill steers, review WARNs, the harness enforces NOTHING):**
- Plan-level: process-start (a full `dotnet test` / build over committed bytes) is **FINE** — it is the
  canonical Full Flight Check. Steer away from network / poll / daemon / live-service probes (review WARNs)
  — a flake there halts the whole run (maximal blast radius).
- Task-level: **prefer** a byte/exit check (runs per task, before the attempt loop); steer away from
  network/poll (review WARNs).
- The property protected is FLAKE-FREEDOM, not process-count. This is **authoring advice**, not a harness
  rule — `guardrails validate`/`run` neither warns nor blocks on a live probe.

**Acceptance criteria (deterministic where possible, else skill golden/meta-test):**
- A breakdown of a brownfield fixture emits `<plan>/preflights/` (with a positive baseline), a non-empty
  `<plan>/guardrails/` carrying ≥1 real integration-set re-run, and a task-level `tasks/<id>/preflights/`
  keyed to a `dependsOn` edge — and it **validates clean** against the new loader.
- No emitted folder contains a `scope:"precondition"` marker or a no-op ROOT/END task.
- `guardrails-review` on a multi-leaf plan with an **empty** `<plan>/guardrails/` emits a **BLOCKER**.
- `guardrails-review` on a plan-level `<plan>/preflights/` containing a full `dotnet test` draws **no
  warning**; on one containing a network/poll probe it emits a **WARN, not a BLOCKER**.
- Skill self-update rules honored: `guardrails-domain-knowledge` updated if the domain model/contract
  facts it carries moved.

**Files:** `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`.

### 10. Example re-author — `docs/plans/09-preflight-first-class/example/`

**What.** Recast the example plan folder to the four-folder model + the new container diagram, so
`guardrails validate` passes against the new loader.
- **Add `<plan>/preflights/`** (plan-root) with **two checks:** one POSITIVE baseline (e.g.
  `01-all-repo-tests-green`) and one NEGATIVE assert-absent baseline (e.g. `02-correlation-id-absent`),
  each a deterministic byte/exit check following the advisory live-probe guidance.
- **Add `<plan>/guardrails/`** (plan-root) holding the terminal whole-repo checks (build + full suite + a
  union invariant), carrying ≥1 real integration-set re-run (re-homed GR2018). **Remove the former
  `integrationGate: true` no-op END task** from `tasks/`.
- **Recast the three former "baseline"/"preflight" tasks:** the global positive baseline (#181, REFRAMED)
  → a `<plan>/preflights/` positive check; the assert-absent → a `<plan>/preflights/` negative check; the
  per-task dependency-delivery illustration (currently `tasks/05-…/preflights/`) stays a task-level
  preflight keyed to its `dependsOn` edge.
- **Do NOT reintroduce a no-op ROOT/END task, its #174/#182 short-circuit scaffolding, or any
  `scope:"precondition"` marker.**
- **Re-run `guardrails validate`** on the re-authored folder (clean, once the loader understands the new
  folders — so this step follows the harness build).
- **Update the example README + plan-09's prose pointer** to describe the re-authored example.

**Acceptance criteria (deterministic):**
- `guardrails validate` on the re-authored example folder exits **0**.
- The example contains all four folder kinds and NO `integrationGate` task, NO no-op ROOT/END task, NO
  `scope:"precondition"` marker (a grep/structure assertion in a meta-test).
- The regenerated `diagram.md`/`diagram.html` match the new container model (golden).

**Files:** `docs/plans/09-preflight-first-class/example/**`.

---

## Wave 3 — Tests (deliverable 11; test-first per deliverable, goldens after 9/10)

### 11. Tests — unit + integration for every phase/outcome, plus the review-named tests

**What.** Author unit + integration tests for every phase and outcome, golden round-trips, and the
specific tests plan-09's review named. **Test-first where it fits** (author the red test before the phase
it gates). **Every phase test runs in serial AND worktree mode** — the serial-mode `IReVerifier` wiring
(deliverable 1) is exactly where a false-green could hide.

**The specific review-named tests (each is an acceptance gate):**
1. **Plan-preflight red → zero-token halt:** RED `<plan>/preflights/` → halt before any task runs, **exit
   2**, zero attempts journaled. (serial + worktree)
2. **B1 resume SKIP:** plan-preflights pass, then a mid-DAG crash + `guardrails run` → the pre-DAG phase is
   **SKIPPED**, the negative baseline is **NOT re-evaluated** (no false-halt on resume). (serial + worktree)
3. **`--fresh` re-runs pre-DAG:** after a passed marker, `--fresh` re-evaluates the pre-DAG phase.
4. **Plan-guardrail red → durable terminal halt:** RED `<plan>/guardrails/` on merged HEAD → terminal
   halt, work durable on the plan branch, **exit 2**. (serial + worktree)
5. **B2 terminal-only resume:** DAG all-green + terminal red + `guardrails run` → all tasks skip (no
   attempt burned), only the terminal phase re-fires. (serial + worktree)
6. **B2 revalidate:** `--revalidate-task plan:guardrails` after a hand-fix → green settle, **exit 0**; a
   still-red gate → `plan-guardrail-failed`, exit 2.
7. **Re-homed GR2018:** a `<plan>/guardrails/` carrying only a tautological `exit 0` file → **validation
   FAILS**; an empty terminal folder on a multi-leaf plan → **validation FAILS**.
8. **Task-preflight no-burn + cone-blocking:** RED `tasks/<id>/preflights/` → `task-preflight-failed` /
   `needs-human` for the cone with **no attempt burned**; independent branches keep running. (serial +
   worktree)
9. **Advisory live-probe guidance:** a plan-level `dotnet test` draws **no** warning; a network probe makes
   `guardrails-review` emit a **WARN, not a BLOCKER**; `guardrails validate`/`run` **neither warns nor
   blocks** either way (the guidance is advisory, not harness-enforced).
10. **Renderer goldens:** container structure + invisible anchors + anchor→anchor edges present; **no
    `done_` node, no fan-out edge**; byte-identical re-render on unchanged input; **edit a
    `<plan>/guardrails/` check → `graph --check` reports stale (exit 2)**.
11. **Journal round-trips:** lossless with and without the `planPreflights`/`planGuardrails` sections; an
    older-shape journal still loads.
12. **Composition-root wiring (deliverable 1):** `IReVerifier` non-null at `maxParallelism: 1` and `> 1`.

**Acceptance criteria:**
- Every deliverable above has at least the tests named for it, all green.
- The full existing suite stays green (no regression).
- The build gate passes (build + test) before completion.

**Files:** `tests/**`.

---

## Wave 3.5 — Migrate the retired-`integrationGate` consumers (from /guardrails-review BLOCKER 1)

### 12. Migrate every committed consumer of the retired GR2017 / `integrationGate` / GR2018 behavior

**What.** The hard-retire in deliverable 2 (GR2017 gone, `integrationGate: true` → hard validation error,
GR2018 re-homed) breaks every existing committed consumer of the OLD behavior. This deliverable migrates
them so the suite stays green under the new loader and no committed artifact is left invalid. Consumers
(from a repo audit — keep the list current at breakdown time):
- **Behavior-pinning tests** (assert the OLD gate rules as *fired* behavior; the new loader breaks them):
  `tests/Guardrails.Core.Tests/ParallelValidationGateTests.cs` — its GR2017 (`no-gate → error`) and GR2018
  (`empty-gate → error`) `[Fact]`s must be **rewritten** to the re-homed folder rule + the new legacy-key
  hard-error diagnostic. (`PlanValidatorTests.cs` / `StagingOutputsValidatorTests.cs` reference GR2017/GR2018
  only as "does NOT fire" — a retired code never fires, so they SURVIVE; confirm, do not rewrite.)
- **Integration regression fixtures** that build a plan with `integrationGate: true`:
  `tests/Guardrails.Integration.Tests/{WiringDefectRegressionTests, NoOpDeadlockShortCircuitTests,
  OverlappingWriteScopeAttributionTests}.cs` — replace each fixture's `integrationGate: true` sink with a
  `<plan>/guardrails/` folder carrying the same terminal check.
- **Committed example/plan folders** (NOT suite-loaded — they don't redden a test, but the new loader
  rejects them, leaving broken committed artifacts): `examples/parallel-hello/**` and
  `docs/plans/08-parallel-execution/**` — migrate the `integrationGate: true` sink task to a
  `<plan>/guardrails/` folder. (`examples/hello-guardrails` is a linear no-gate chain — UNAFFECTED.)

**Bootstrap exemption (name it).** THIS plan's OWN terminal gate — the `integrationGate: true` sink the
current (pre-migration) `plan-breakdown` generates for `preflights-impl` — is **NOT migrated**. It MUST
stay `integrationGate: true` because the harness EXECUTING this dogfood is the shipped tool (preview.34),
which predates the new loader; the `<plan>/guardrails/` folder is inert under it. So this plan builds a
loader that would reject its own terminal gate — unavoidable for a bootstrap dogfood. Deliverable 12's
writeScope therefore **EXCLUDES `docs/plans/preflights-impl/**`**; no test loads this plan's own folder, so
nothing needs to catch the exemption during the run.

**Ordering.** `dependsOn` the loader (deliverable 2) — it needs the retire to have landed. The terminal
gate (this plan's own sink) MUST `dependsOn` deliverable 12, so the whole suite runs only AFTER the
migrated tests are green. Between deliverable 2 (which reddens `ParallelValidationGateTests`) and
deliverable 12, no guardrail runs the affected tests (deliverable 2's own test guardrail is `--filter`-scoped
to the FourFolder tests), so the transient red is invisible and healed before the terminal gate.

**May split (size, #111).** This bundles two surfaces (test migration + example-plan migration); if it
trips the plan-breakdown Step 2 split-trigger, split into **12a** (behavior-pinning + regression tests) and
**12b** (example plans). Both `dependsOn` deliverable 2; the terminal gate `dependsOn` both.

**Acceptance criteria (deterministic):**
- The full existing suite passes under the new loader (`dotnet test Guardrails.sln`), including the
  rewritten `ParallelValidationGateTests` and the 3 migrated regression fixtures.
- `guardrails validate examples/parallel-hello/parallel-hello` and `guardrails validate
  docs/plans/08-parallel-execution` each exit **0** under the new loader (migrated to the folder model).
- **No `integrationGate: true` remains** in any migrated consumer (a negative grep over `tests/**`,
  `examples/parallel-hello/**`, `docs/plans/08-parallel-execution/**` — but NOT `docs/plans/preflights-impl/**`,
  the exemption).
- The rewritten validator-gate tests assert the NEW rules: an empty/tautological `<plan>/guardrails/` on a
  multi-leaf plan FAILS; a legacy `integrationGate: true` yields the new hard-error diagnostic.

**Files:** `tests/Guardrails.Core.Tests/ParallelValidationGateTests.cs`,
`tests/Guardrails.Integration.Tests/{WiringDefectRegression,NoOpDeadlockShortCircuit,OverlappingWriteScopeAttribution}Tests.cs`,
`examples/parallel-hello/**`, `docs/plans/08-parallel-execution/**`. **Explicitly NOT**
`docs/plans/preflights-impl/**` (bootstrap exemption).

---

## Guardrail-strength requirements (from /guardrails-review — the breakdown MUST honor these)

A `/guardrails-review` adversarial pass (3 devil's-advocate agents + lead verification) on the first
generated DAG surfaced the findings below. Each is folded in as an ADDITIONAL acceptance criterion on the
named deliverable — the regenerated guardrails MUST satisfy them (they are the difference between the first
DAG and this one):

- **D6 (outcomes+journal) — the §7 SSOT edit MUST be GATED (was ungated — BLOCKER 2).** The implement task
  landing the three outcomes MUST carry a deterministic SSOT-contains guardrail asserting all three outcome
  names (`plan-preflight-failed`, `task-preflight-failed`, `plan-guardrail-failed`) appear in
  `02-schemas-and-contracts.md` §7 — mirroring the SSOT guardrails on D2/D3/D4. Without it an impl lands the
  enum + journal with zero contract prose, green.
- **D2 (loader) — the brownfield green-baseline is PER-PROJECT, not whole-solution (WEAK).** This plan
  touches TWO test projects: `Guardrails.Core.Tests` (loader/model/journal/renderer) and
  `Guardrails.Integration.Tests` (phases/wiring). Emit the #181 positive baseline as TWO roots —
  `dotnet test tests/Guardrails.Core.Tests --filter "Category!=Preflights"` and the same for
  `Guardrails.Integration.Tests` — each gating its own subtree, NOT one whole-solution
  `dotnet test Guardrails.sln` root (which serializes the DAG and blurs attribution).
- **D2 (loader) — GR-code allocation via the committed `DiagnosticCodes.cs` stub, NOT run state (WEAK).**
  The author-tests task writes the contiguous GR2027+ `const` codes into `DiagnosticCodes.cs`; the implement
  task reads them from there. Do NOT emit a `grCodes` state fragment — nothing gates or consumes it (theater).
- **D2 (loader) — the author-tests `covers-key-behaviors` MUST carry a token for the TAUTOLOGICAL-terminal
  scenario (WEAK — this is the B3 content teeth).** The most gameable acceptance criterion (`<plan>/guardrails/`
  with only an `exit 0` file must FAIL) needs its own distinctive token, else an author drops that scenario
  while the empty-folder scenario satisfies the grep.
- **D8/D2 (SSOT guardrail) — assert the §3.3 FACTS, not just the folder token (WEAK).** The SSOT-updated
  guardrail must assert `integrationGate` (the retirement) AND `GR2018` (the re-home) are documented — not
  merely that `preflights/` + `GR2027` appear (a two-token presence grep an impl satisfies without landing
  the §3.3 prose).
- **D5 (task-preflight) — the author-tests `covers-key-behaviors` MUST require the NO-BURN assertion shape
  (WEAK).** The whole feature rests on "preflight fails WITHOUT burning an attempt" — the covers check must
  require the test asserts zero attempt-count increment (e.g. match `attempt.*(0|zero|not.*increment|Empty)`),
  not just that the word "attempt" appears.
- **D7 (renderer) — the §10 SSOT guardrail MUST carry a NEGATIVE assertion that the OLD model is REMOVED
  (WEAK).** A presence-only `-match anchor` passes a half-migrated §10 that still describes `done_`/fan-out.
  Add a fail-on-present check for the retired `done_<id>` / `task --> guardrail` description, scoped to §10.
- **D7 (renderer) — the renderer-tests guardrail MUST assert the container goldens were actually SELECTED
  (WEAK).** A `FullyQualifiedName~Diagram` filter matching ZERO tests (e.g. after a class rename) passes green
  on an old-model renderer. Add a floor asserting the container-goldens class actually ran.
- **D10 (example) — the meta-test MUST actually assert NO no-op ROOT/END task AND that the terminal folder
  has TEETH (WEAK×2).** The `legacy-absent` check must genuinely assert no no-op ROOT/END task exists (not
  merely claim it in a comment); and the example's `<plan>/guardrails/` must be asserted to carry ≥1 REAL
  integration-set re-run (a build/suite/union command), not just a non-empty folder — else the worked example
  is itself the tautology the feature exists to eliminate.
- **D9 (skills) — the skill-doc guardrails scope to `SKILL.md` (the procedure), not "anywhere under the
  skill dir" (WEAK).** A token-presence check over `**/*.md` is satisfiable by a stub in `references/`; require
  the four-folder tokens + the BLOCKER-probe language appear in `SKILL.md` itself.
- **Terminal gate — the `scope:"integration"` union invariant MUST assert CONTRIBUTION-PRESENCE + a
  DUPLICATE-DEFINITION count on the shared multi-writer files (WEAK).** `02-schemas-and-contracts.md` is
  edited by ≥5 tasks across waves (the genuine parallel multi-writer). Conflict-marker-free + non-empty is not
  enough: add union-safe (present-gated) checks that each distinctive contribution survived the union, and a
  `[regex]::Matches(...).Count -gt 1` duplicate-definition check on shared CODE files (`Scheduler.cs`) — a
  3-way merge keeps two copies with no conflict marker (CS0101).
- **Terminal gate — the full-suite guardrail SHOULD assert a STRICTLY-POSITIVE test count (NIT).** Guard
  against a discovery drop silently passing zero tests.
- **D5 (task-preflight) — insert the preflight gate BEFORE `journal.MarkRunning` (NIT).** Avoid a transient
  `Running → needs-human` with zero attempts in `run.json`.
- **D4 (terminal phase) — the `--revalidate-task plan:guardrails` red test drives the EXISTING string CLI
  arg, not a new symbol (NIT).** So the author-tests red compiles before deliverable 4 adds the reserved-id
  handling.

---

## Notes / implementation calls flagged for the lead

Places where plan-09 leaves an implementation choice open (called out per the brief so the lead can
confirm during breakdown/review — none block the WHAT):

1. **GR code numbering.** plan-09 says "GR2027+" for the four-folder malformed-declaration diagnostics
   and the re-homed GR2018 replacement, but does not enumerate the exact codes or their count. I left the
   allocation to the harness-developer at implementation time ("next-free, contiguous, documented in the
   SSOT"). The lead should confirm GR2026 is still the last taken and that GR2027+ is free when the change
   lands. **Implementation call:** exact codes deferred to implementation.

2. **The re-homed GR2018 "counts toward the terminal gate" marker.** plan-09 explicitly leaves the
   mechanism open: the terminal folder's real-integration-set-re-run check "may reuse the §4.3
   `scope:"integration"` tag as the marker on the folder's files, OR introduce a folder-scoped equivalent
   — either way the content obligation survives." I did NOT pick one in this plan; it is a genuine
   design-within-implementation call for the harness-developer, and the acceptance criterion is written to
   the OBLIGATION (≥1 real re-run), not the mechanism. **Implementation call:** tag-reuse vs
   folder-scoped-marker deferred.

3. **How the retired `integrationGate: true` key is handled on an old plan** — **RESOLVED (lead decision,
   post-review): a HARD validation error** (a new GR2027+ unsupported-key diagnostic), no coexistence
   window. Because that hard-retire breaks every existing committed consumer of the old behavior,
   **deliverable 12** migrates them (behavior-pinning tests + regression fixtures + the `parallel-hello` /
   `08-parallel-execution` example plans), and **this plan's own terminal gate is a named bootstrap
   exemption** (it must stay `integrationGate: true` to run under the shipped preview.34 harness — see
   deliverable 12).

4. **`plan:preflights` scope in v1.** plan-09 mints `plan:preflights` (the preflight analogue of
   `plan:guardrails`) as a "should a human want to re-confirm a hand-fixed starting state" affordance, but
   the B1 marker + `--fresh` already cover the resume path. I included it in deliverable 4 as a mint
   alongside `plan:guardrails` for symmetry, but its user-facing utility is thinner than
   `plan:guardrails`. **Implementation call:** whether `--revalidate-task plan:preflights` ships in v1 or is
   reserved-but-inert — lead to confirm (I'd ship both for symmetry; low cost).

5. **Renderer prototype gate.** plan-09 is emphatic that the Mermaid anchor technique must be prototyped
   against the bundled version BEFORE committing to the container model, but a task DAG cannot easily gate
   "prototype first" deterministically. I encoded it as a first renderer sub-deliverable with a
   prototype/spike acceptance artifact; the lead may prefer to run the prototype spike manually before the
   renderer task enters the DAG. **Implementation call:** whether the prototype is a DAG task or a manual
   pre-step deferred.

---

## Reference

The design-of-record — **consult it for every WHY/detail/rationale** — is
`docs/plans/09-preflight-first-class.md` (PLANNED, plan-root, hardened, advisory; tip `513cd5d`). The
schema SSOT this build edits is `docs/plans/02-schemas-and-contracts.md` (§1 layout, §3.3
`integrationGate` migration, §4.3 union re-verify, §7 journal/outcomes, §7.1 revalidate, §10 renderer).
