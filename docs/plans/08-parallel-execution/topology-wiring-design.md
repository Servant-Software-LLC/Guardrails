# Architecture: Wiring the worktree-topology methods into the Scheduler

> **Status:** design-of-record for "wire the topology" (F2 resolved — the PO chose *wire*, not
> *delete*). Input to a developer (`guardrails-harness-developer`) + test author
> (`guardrails-test-author`). Produces designs/decisions; no production code here.
> Scope: close the gap where `ReuseSegment` / `ForkFromTip` / `CreateFanIn` / `Discard` /
> `PruneOrphans` are defined on `IWorktreeProvider` and implemented on `GitWorktreeProvider`
> but **never called** by `Scheduler`. Closes **#126** (segment worktrees not pruned post-run).
>
> **One-line recommendation:** wire **ReuseSegment + ForkFromTip (M1)** and **Discard +
> PruneOrphans (M2)**; **DEFER `CreateFanIn` (M3, recommend NOT building in v1)** — the existing
> plan-branch union path already produces correct fan-in, and `CreateFanIn` adds a redundant
> private merge plus a non-FF re-integration without removing a union. The tradeoff is made
> explicit below so the lead can overrule.

---

## What's being asked

`GitWorktreeProvider` ships real implementations of a worktree-reuse topology — `ReuseSegment`
(branch-switch reuse of a producer's directory), `ForkFromTip` (fork a fresh tree off a producer's
**recorded** sha), `CreateFanIn` (private pre-merge of producers — currently `throw new
NotImplementedException`), `Discard` (`git worktree remove --force`), `PruneOrphans` (`git worktree
prune`). The `Scheduler` calls **none** of them. It currently calls only:

- `CreateSegment` — for the initially-ready set (`Scheduler.cs:160-161`) and **lazily** for each
  unlocked dependent (`Scheduler.cs:335-336`), so every task gets a **fresh** segment off the
  current plan-branch tip;
- `Integrate` / `CommitStagedMerge` / `RollbackMerge` — the settle (`Scheduler.cs:410`, `458`, `491`);
- `MergePlanBranchIntoUserBranch` — end-of-run delivery (`Scheduler.cs:223`);
- `PruneStaleRunBranches` / `ReconcileFromPlanBranch` — resume pre-pass (`Scheduler.cs:110-111`).

So the topology methods are dead code. "Wire the topology" = make the reuse/fork/fan-in/cleanup
methods load-bearing, **and decide which of them earns its place** given the current model already
produces correct outcomes.

### Ambiguity named & narrowed

- **"Wire the topology" read literally = wire all five.** I narrow it: ReuseSegment + ForkFromTip +
  Discard + PruneOrphans have **self-contained value over the current model** (disk/checkout
  reduction; #126 cleanup) and are wired. `CreateFanIn` is an **alternative fan-in mechanism that
  competes with a path that already works** (the plan-branch union) — I recommend deferring it and
  flag the divergence from a literal reading for the lead (Decision F below). This is the one place
  I diverge from "wire everything," and I say so loudly rather than bundling its risk in.
- **"Reuse" means reuse the producer's *worktree directory*, not its branch identity.** The
  inheritor commits onto the producer's segment *branch*, advancing it; siblings fork fresh off the
  producer's **recorded** sha (W-2). I make the inheritor/sibling predicate exact in §Design.
- **Marginal-value honesty (required by the brief).** Each wired method is justified on its own
  merits below; ReuseSegment/ForkFromTip are an **optimization** over a correct baseline, not a
  correctness fix. I do not oversell them.

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

| Concern | Placement |
|---|---|
| Reuse/fork inheritor predicate + call-site in `OnSettledAsync` (lazy dependent creation) | **harness** — `Scheduler.cs` |
| `Discard` on permanent-failure / cancel; `PruneOrphans` at run start + end (#126) | **harness** — `Scheduler.cs` (+ a tiny `IWorktreeProvider` semantics tightening) |
| `CreateFanIn` private pre-merge as the fan-in mechanism | **v2 / defer** — recommend NOT v1 (Decision F). If the lead overrules: harness + a real `IntegrationResult` re-integration contract |
| Tracking which live tasks own which worktree directory (reuse bookkeeping) | **harness** — new per-run map in `Scheduler.RunContext` |
| SSOT note that reuse/fork/cleanup are now wired; `CreateFanIn` status | **schema/docs** — `02-schemas-and-contracts.md` §3.2 (small delta, §SSOT below) |
| Metamorphic "reuse reduces `git worktree add` count" assertion | **test** — `guardrails-test-author` |

No new `guardrails.json` field, no new env var, no new GR code. This is a **wiring + scheduling**
change, not a contract expansion — which is itself an argument for keeping it tight.

---

## Invariants in play

Named, with how the wiring respects or strains each (the brief requires ≥2; I assess four).

1. **(#2) Harness is the single writer of merged state; children get snapshots, write fragments.**
   *Respected, and the load-bearing one for reuse.* Reuse changes *where a task's child process
   runs* (an inherited directory) — it does **not** change who writes the plan branch. The plan
   branch is still written only by `Integrate`/`CommitStagedMerge` under `_integrationLock`. The new
   risk reuse introduces is a *physical* one — two tasks could be told to use the same directory —
   and the inheritor-predicate proof (§Design) is exactly the discharge of that risk: the producer
   is **fully settled** before its inheritor is created, so the directory is never concurrently held.

2. **(W-2 retry-reset) A retry resets the segment to `TaskBase`, discarding only this task's WIP,
   never upstream commits.** *Respected, and the reason ForkFromTip must use the recorded sha.* On a
   reused directory, `ReuseSegment` sets `TaskBase = upstream.RecordedCommitSha`
   (`GitWorktreeProvider.cs:92`), so `TaskExecutor.ResetSegment(worktree.TaskBase)`
   (`TaskExecutor.cs:115-118`) discards only the inheritor's WIP and keeps the producer's commit
   (it is `TaskBase` itself). A sibling that forks via `ForkFromTip(producerRecordedSha, …)` forks
   off the **recorded** sha, never `rev-parse <segmentBranch>` — because the inheritor may have
   already advanced that branch's tip (W-2, `GitWorktreeProvider.cs:97-101`).

3. **(deferred-settle ordering under `_integrationLock`) Dependents become ready only after the
   upstream integration advanced the plan branch.** *Strained by reuse, and the subtlety to get
   right.* Today the lazy `CreateSegment` at `Scheduler.cs:335-336` runs **inside `lock(_gate)`**,
   *after* `SettleAsync` already ran under `_integrationLock` (lines 302-311). `ReuseSegment` is a
   pure handle rewrite (no git) so it is safe under `_gate`. **But `ForkFromTip` does a real
   `git worktree add`** (`GitWorktreeProvider.cs:102-119`) — running git I/O under `_gate` (held by
   every settling worker) would serialize forks behind the gate. §Design moves the actual
   `worktree add` **out of the gate** (compute the handle assignment under `_gate`, materialize the
   tree lazily at dequeue / in the worker), matching how the design-of-record already specifies
   "lazily at dequeue (so a never-scheduled branch never pays a checkout)" (08 §1, line 236).

4. **(#5 honest halts) Nothing marked done unverified.** *Respected.* Reuse/fork/cleanup touch no
   verdict. The only honest-halt-adjacent wiring is `Discard` on permanent failure — and it must run
   **only after** the task is journaled `needs-human`/`blocked`, never instead of journaling, and
   **never** on a directory an inheritor holds (the bug this design's bookkeeping prevents).

The reason I recommend **deferring CreateFanIn** is invariant 1: it adds a second writer-shaped
merge (the private pre-merge) whose result must still pass through the single-writer settle, and the
re-integration of that private merge onto the plan branch is non-FF by construction (§Decision F) —
more integration surface, not less.

---

## Current model, restated precisely (verified by reading)

**Fresh segment per task, dependents created lazily after upstream settle.**

- Run start: `CreateIntegration` once (`Scheduler.cs:97-100`). Resume pre-pass prunes this run's
  stale segment branches and reconciles plan-branch trailers (`Scheduler.cs:108-112`).
- Initially-ready tasks (no pending deps) get a fresh segment off the plan-branch tip
  (`CreateSegment`, `Scheduler.cs:158-164`) and are written to the channel (`:166-172`).
- A worker runs the task (`_executor.ExecuteAsync`, `Scheduler.cs:245`) then `OnSettledAsync`
  (`:247`).
- **Settle (worktree mode, green, deferred):** under `_integrationLock` (`Scheduler.cs:302`),
  `SettleAsync` (`:392-513`) does fragment-merge → `Integrate` (FF | non-FF union | conflict→AI-merge)
  → re-verify (non-FF / AI paths) → `CommitStagedMerge` → `RecordSettle`. `Integrate`
  (`GitWorktreeProvider.cs:134-174`) stages+commits the segment, **captures
  `segment.RecordedCommitSha` at line 149** (before the FF/non-FF branch), then FFs the plan branch
  or stages a `--no-commit` union.
- **Lazy dependent creation:** still inside `OnSettledAsync`, under `lock(_gate)` (`Scheduler.cs:322`),
  for each dependent whose last pending dep just cleared (`:331`), a **fresh** segment is created
  **now** — after the plan branch advanced — so the dependent forks from the upstream's integrated
  HEAD (`:335-338`). This is exactly what makes a linear chain FF: B's segment is off A's integrated
  tip.

**Consequences already true without any topology wiring:**

- **Linear chain A→B→C already fast-forwards.** Each settle FFs (`SettleAsync` line 412-418); B's
  fresh segment is off A's integrated HEAD, so B→plan-branch is again a FF. N free FF-integrations.
- **Fan-in already works via the plan-branch union.** Producers P1…Pk each settle (their work lands
  on the plan branch — FF for the first, non-FF union + re-verify for the racers). The fan-in task
  F's pending-dep count reaches zero only when the **last** producer settles (`Scheduler.cs:331`),
  at which point F's fresh segment is forked off a plan-branch tip that **already contains all
  producers' unioned work**. F sees the merged tree. No `CreateFanIn` is needed for correctness.
- **W-2 retry-reset** resets a fresh segment to its `TaskBase` (the plan-branch tip it forked from);
  `TaskExecutor.cs:115-118`.

So the topology methods are an **optimization + an alternative fan-in mechanism over a model that is
already correct.** That framing drives every decision below.

---

## Design

### Per-method wiring decision

#### A. `ReuseSegment` — the disk lever (WIRE, M1)

**What it physically is:** no git op — a pure `WorktreeHandle` rewrite that points the inheritor at
the producer's **directory** and **branch**, with `TaskBase = upstream.RecordedCommitSha`
(`GitWorktreeProvider.cs:87-95`). The inheritor's child process runs in the producer's tree and
commits on top; `Integrate` later FFs that commit onto the plan branch.

**Exact policy / predicate (the inheritor rule).** In `OnSettledAsync`, when producer `P` settles
green and we iterate `context.Graph.DependentsOf(P)` clearing pending deps (`Scheduler.cs:329-341`),
a dependent `D` **reuses `P`'s segment directory** iff **all** of:

1. `D` has exactly one producer — `D.DependsOn.Count == 1` (equivalently
   `graph.TransitiveDependenciesOf(D)` direct-parent count is 1; use the direct `DependsOn`). A
   multi-producer `D` is a fan-in and never reuses (it needs the merged tree, not one producer's
   tree).
2. `P` has not already given its directory away **this settle**. Among `P`'s dependents that clear
   to ready in this `OnSettledAsync` call, **at most one** inherits.
3. **Tiebreak when `P` has ≥2 single-producer dependents clearing at once (fan-out):** the inheritor
   is the dependent with the **longest downstream chain**, ordinal-id tiebreak (08 §1 line 235:
   "longest-downstream-chain successor (ordinal-id tiebreak)"). Longest chain = the dependent
   maximizing `graph.TransitiveDependentsOf(d).Count`; ties broken by `string.Ordinal` on id. The
   **other** single-producer dependents **fork** (rule B). Multi-producer dependents are untouched
   by this rule (fan-in).

**Why the inheritor can never start before the producer's directory is free (the required proof).**
`OnSettledAsync` creates `D`'s handle only on the path where `P` settled green
(`Scheduler.cs:327-341`), which is reached **after** `SettleAsync` returned (it ran under
`_integrationLock` at `:302-311`, before the `lock(_gate)` block at `:322`). `SettleAsync` is the
*whole* of `P`'s integration — `Integrate` committed `P`'s segment and FF'd/union'd it onto the plan
branch, and `RecordSettle` journaled it. After that returns, **no further code runs `P`'s action or
touches `P`'s tree** — `P` is terminal. The inheritor `D` is the *only* task that will ever touch
that directory again, and it does not exist until this exact point. There is no window in which both
`P` and `D` hold the directory: `P` is done the instant before `D` is born. (Contrast the broken
alternative — reusing a directory whose producer is still *retryable* — which §Bookkeeping below
makes structurally impossible by only ever reusing a settled producer's tree.)

**Call-site.** `Scheduler.OnSettledAsync`, inside `lock(_gate)`, replacing the unconditional
`CreateSegment` at `:335-336`:

```
choose inheritor among the just-cleared single-producer dependents of P (rule A1-A3)
for each newly-ready dependent D:
    if D is the chosen inheritor:  depHandle = provider.ReuseSegment(P's handle, D.Id, attempt:1)
    else if D is single-producer:  depHandle = ASSIGN a fork (rule B) — materialized off-gate
    else (multi-producer fan-in):  depHandle = CreateSegment(D, ...) off the plan-branch tip (unchanged)
```

`ReuseSegment` is pure (no git), so it is safe under `_gate`. The producer's handle is available:
`context.Handles[P.Id]` (the map already exists, `Scheduler.cs:155`), and its `RecordedCommitSha`
was set by `Integrate` during `SettleAsync` (`GitWorktreeProvider.cs:149`).

**Marginal value (honest):** fewer `git worktree add` calls and less peak disk for deep linear
chains and the inherited leg of a fan-out — exactly the brief's "disk lever." It does **not** change
any verdict or the FF behavior (the chain already FF'd). For a pure linear chain of N tasks this
turns N `worktree add` calls into **1**.

#### B. `ForkFromTip` — the sibling case (WIRE, M1)

**What it physically is:** `git worktree add -b <forkBranch> <forkPath> <producerRecordedSha>`
(`GitWorktreeProvider.cs:102-119`) — a fresh tree off the producer's **recorded** sha.

**Policy.** The single-producer dependents of `P` that did **not** win the inheritor tiebreak (rule
A3) fork via `ForkFromTip(P.RecordedCommitSha, D.Id, attempt:1)`. They must fork off the **recorded**
sha (the producer's committed tip captured at `Integrate`), **not** the live segment branch tip,
because the inheritor has advanced that branch (W-2; `GitWorktreeProvider.cs:97-101`, design 08 §1
lines 236-243).

**Lock discipline (the subtlety from invariant 3).** `ForkFromTip` does real git I/O. Running it
inside `lock(_gate)` would serialize every fork behind the gate that all settling workers contend
for. Two acceptable shapes; I recommend the second:

- *(simple)* materialize the fork inside `_gate` — correct but serializes fork I/O. Acceptable for
  v1 if measured contention is negligible (forks are O(fan-out width), rare).
- *(recommended)* under `_gate`, store a **deferred fork request** on the envelope
  (`producerRecordedSha + taskId`); the **worker** calls `ForkFromTip` at dequeue, before
  `ExecuteAsync`. This matches the design-of-record's "lazily at dequeue (so a never-scheduled
  branch never pays a checkout)" (08 §1 line 236) and keeps git I/O off the gate. Concretely:
  `TaskEnvelope` gains an optional `ForkRequest? Fork`; `WorkerLoopAsync` (`Scheduler.cs:234-248`),
  if `envelope.Fork is { } fr`, calls `handle = provider.ForkFromTip(fr.Sha, task.Id, 1)` before
  `ExecuteAsync`. (`CreateSegment` for the initially-ready set and for fan-in dependents stays where
  it is — those are off the hot gate already.)

**Marginal value (honest):** correctness-neutral vs the current fresh-`CreateSegment`-off-plan-tip
(both give the sibling a correct base — the plan tip already contains `P`). The win is **the fork
point**: `ForkFromTip` roots the sibling at `P`'s recorded sha so the sibling's diff is minimal
(only `P`'s ancestry, not other siblings that settled onto the plan branch meanwhile), which keeps
its eventual integration a cleaner FF/small-union. This is a real but **modest** improvement;
if M1 proves it adds scheduling complexity without measured benefit, falling back to
`CreateSegment` off the plan tip for siblings is a safe degrade (noted as a risk).

#### C. `Discard` — cleanup on permanent failure / cancel (WIRE, M2; part of #126)

**Policy.** `Discard(handle)` (`git worktree remove --force`, `GitWorktreeProvider.cs:203-206`) runs
when a task reaches a **permanent** non-green outcome and **no live task holds its directory**:

- In `OnSettledAsync`, after a task settles `needs-human`/`failed` (the `else if (result.Outcome !=
  Cancelled)` branch, `Scheduler.cs:343-364`) **and** after its transitive dependents are journaled
  `blocked` — Discard the failed task's own segment **iff** it is not an inheritor-held shared
  directory still owed to a (now-blocked, never-to-run) dependent. Since a blocked dependent never
  runs, the directory is free; Discard it.
- On cancellation (`result.Outcome == Cancelled`), **do not Discard** — the task is journaled
  `pending` for resume and the resume prune (`PruneStaleRunBranches`) handles it. Discarding here
  would race the executor's in-flight teardown.

**Why this is safe against the reuse bookkeeping:** Discard consults the per-run `directoryOwner`
map (§Bookkeeping). A directory is only Discarded when its current owner is the failing task **and**
no inheritor was assigned (a failing task's inheritor is never created — failure takes the
`else if` branch at `:343`, which blocks dependents, it does not create them). So the inheritor/
Discard paths are mutually exclusive by construction.

#### D. `PruneOrphans` — defensive cleanup (WIRE, M2; closes #126)

**Policy.** `PruneOrphans` (`git worktree prune`, `GitWorktreeProvider.cs:209-212`) runs:

1. **At run end**, after the report is built and any `MergePlanBranchIntoUserBranch` — a final
   `provider.Discard` of every still-live segment directory the run created, then `PruneOrphans` to
   clear registrations. This is the **direct fix for #126** ("segment worktrees not pruned post-run").
   The integration worktree is **reattached, never pruned** (design 08 §7 line 962) — so the end-of-run
   sweep removes only `guardrails/<runId>/<task>/…` segment/fork trees, never `_integration`.
2. **Optionally at run start**, before workers launch, as belt-and-suspenders against a prior crashed
   run's orphans. Note this **overlaps** `PruneStaleRunBranches` (`Scheduler.cs:110`), which already
   deletes this run's stale segment branches+worktrees. To avoid redundancy I recommend **#126 is
   closed by the run-end sweep alone**, and the run-start `PruneOrphans` is **optional** (low value
   given `PruneStaleRunBranches` already runs). Pick one; do not double-prune.

**`Discard`/`PruneOrphans` are idempotent and best-effort** — wrap each in try/catch that logs and
continues (a cleanup failure must never fail an otherwise-green run). `GitWorktreeProvider.Discard`
currently throws on a non-zero git exit; M2 should make the **call-site** swallow-and-log, or add an
idempotent `Discard` overload. (Smallest change: call-site try/catch.)

#### E. `CreateFanIn` — the private pre-merge (DEFER — recommend NOT v1; Decision F)

See the fan-in recommendation below. If wired, it is M3, gated, and carries the re-integration
contract spelled out there.

### Reuse bookkeeping (new per-run state)

`RunContext` gains one map: `Dictionary<string,string> DirectoryOwner` keyed by **worktree path** →
**current owning task id** (or, equivalently, augment the existing `Handles` map's semantics). It is
written under `_gate` only:

- `CreateSegment` / `ForkFromTip` → `DirectoryOwner[path] = taskId`.
- `ReuseSegment` → `DirectoryOwner[producerPath] = inheritorId` (ownership transfers; the producer
  is settled).
- `Discard` (failure/cancel/end) → only when `DirectoryOwner[path] == thatTaskId` and the task is
  terminal; then remove the entry.

This map is the single source of truth for "is this directory free to Discard / reuse?" — it makes
the §A proof and the §C exclusivity claim mechanical rather than argued.

### Seams and contracts touched

- **`IWorktreeProvider`** — no signature change. (`ReuseSegment`/`ForkFromTip`/`Discard`/
  `PruneOrphans` already exist with the right shapes.) Optional: a tiny doc-comment tightening on
  `Discard` ("caller guarantees no live task holds this worktree").
- **`Scheduler.cs`** — `OnSettledAsync` inheritor/fork selection; `WorkerLoopAsync` deferred-fork
  materialization; `RunContext.DirectoryOwner`; `TaskEnvelope.Fork`; end-of-run cleanup sweep.
- **`FakeWorktreeProvider`** — already implements all five no-op-style (`FakeWorktreeProvider.cs:30-63`);
  its `ReuseSegment`/`ForkFromTip` return distinct placeholder paths, so unit tests can assert which
  path was taken **without git**. No change needed beyond possibly recording call counts (test seam).
- **No `IProgressSink`/`IActionRunner`/`IReVerifier` change.** Reuse is invisible to the executor —
  it receives a `WorktreeHandle` and runs in `handle.WorktreePath` regardless of how it was made
  (`TaskExecutor.cs:188-191`).

### Schema changes (exact `02-schemas-and-contracts.md` edits)

This is a wiring change, not a contract change — **no new field, env var, or GR code.** The only SSOT
edit is a precision tightening of §3.2 so the doc matches the now-wired behavior (and records that
`CreateFanIn` is deferred). Replace, in §3.2 "Worktree task semantics", the sentence:

> a fan-out inherits one chain and forks the rest off the producer's committed tip; a fan-in forks
> one upstream and merges the others in (§5.3 union).

with:

> a fan-out **inherits one** chain (the longest-downstream successor reuses the producer's segment
> worktree directory; ordinal-id tiebreak) and **forks the rest** off the producer's **recorded**
> committed sha (never the live segment-branch tip, which the inheritor may have advanced); a
> **fan-in** task forks a fresh segment off the **plan-branch tip**, which already contains every
> producer's integrated work (the producers' own settles unioned it onto the plan branch), so the
> fan-in sees the merged tree without a separate private merge. *(A private pre-merge worktree
> — `CreateFanIn` — is defined on the provider but is **not wired in v1**; the plan-branch union is
> the v1 fan-in mechanism. See plan 08 `topology-wiring-design.md` Decision F.)*

If the lead **overrules** Decision F and wires `CreateFanIn`, the §3.2 edit instead reads "a fan-in
forks one upstream into a private worktree and merges the others in before the task runs," and a new
§5.3 paragraph must specify the private-merge re-integration contract (drafted in Decision F).

---

## The fan-in recommendation (a / b / c)

**Recommendation: (b) — keep the existing plan-branch union path; wire only reuse + fork + cleanup.
Defer `CreateFanIn` (recommend deleting its `throw`-stub or leaving it dead behind an explicit "not
v1" doc-comment).** One-line why: **the union path already produces a correct merged tree for the
fan-in task, and `CreateFanIn` adds a redundant private merge plus a guaranteed non-FF
re-integration without removing any union — more integration surface and a new resume/trailer edge,
for no correctness gain.**

**The full argument.**

- **The union path already covers fan-in correctly (proven above).** When fan-in `F`'s last producer
  settles, `F`'s fresh segment forks off a plan-branch tip that already contains *all* producers'
  unioned, re-verified, trailer-bearing work. `F` runs against the merged tree; `F`'s own
  integration is a clean FF (nothing raced it) or a small non-FF union (a true concurrent sibling) —
  both already handled by `SettleAsync` (`Scheduler.cs:410-513`).

- **What `CreateFanIn` would *add*, concretely.** It forks a **private** worktree off one producer,
  merges the others in **before `F` runs** (git auto → AI-merge → re-verify, design 08 §4), so `F`
  sees the merged tree *and could resolve as part of its own work*. But that merged tree's commit has
  the producer segment tips as parents — it is **not an ancestor of the plan-branch tip** (the plan
  tip reached the same content by a *different* topology: each producer's own settle). So when `F`'s
  segment (rooted on that private merge) integrates, `Integrate`'s `--ff-only` (`GitWorktreeProvider.cs:154`)
  **must fail**, forcing the non-FF union path + a re-verify — a union the plan-branch path did
  **once already**, redone. `CreateFanIn` *moves* the merge earlier; it does not *remove* it.

- **It also opens a new resume/trailer edge.** The current model's resume authority is
  "trailer-bearing commits reachable from the plan-branch tip" (design 08 §7). The producers' work is
  on the plan branch via their own settles regardless. A private fan-in merge introduces a commit
  whose relationship to the plan branch is "merged-in-later, non-FF" — fine, but it is **new
  topology to reason about on crash-resume** for zero correctness benefit, when the brief's own risk
  history is two false-greens from exactly this kind of added merge complexity.

- **The honest counter (why someone wants `CreateFanIn`):** letting `F` *see and resolve* the
  producer union as part of its own action is genuinely nicer when producers conflict — the agent
  doing `F` has the most context to resolve. But v1 already routes a fan-in conflict through the
  **AI-merge worker + re-verify** at each producer's non-FF settle (design 08 §4), which is the
  sanctioned conflict path. Giving `F` the resolution job is a **different** (not obviously better)
  policy that the design-of-record did not commit to, and it is exactly the prompt-in-the-loop
  surface invariant 1 is most nervous about. Deferring keeps v1's conflict story to **one**
  mechanism.

- **If the lead overrules** (wants `CreateFanIn` in v1): it is **M3, strictly after M1/M2 ship and
  are green**, and it requires a **new re-integration contract** that `Integrate` does not currently
  express: integrating a fan-in segment whose `TaskBase` is a non-ancestor private merge must be a
  *recognized* non-FF union with the colliding-sibling re-verify set (design 08 §4 step 3), and the
  merge commit must carry `F`'s `Guardrails-Task:` trailer via `CommitStagedMerge`
  (`GitWorktreeProvider.cs:183-189`) so resume reconcile still holds. That is a real schema/contract
  paragraph in §5.3, not a free wiring. I would still re-verify (option b) is enough — but this is
  the buildable shape if overruled.

So: **(b)**, with **(c)/`CreateFanIn` explicitly deferred** and the tradeoff on the table.

---

## Interaction proofs (per wired method)

| Method | W-2 retry-reset | Lock ordering (`_integrationLock` / `_gate`) | FF-compatibility | Resume reconcile (plan-branch trailers) |
|---|---|---|---|---|
| **ReuseSegment** | `TaskBase = producer.RecordedCommitSha` → reset discards only inheritor WIP, keeps producer commit (it *is* `TaskBase`). `TaskExecutor.cs:115-118` unchanged. | Pure handle rewrite; safe under `_gate`. No git I/O on the gate. Runs *after* producer's `SettleAsync` (under `_integrationLock`) returned — strict happens-after. | Inheritor commits on top of producer's tip; its `Integrate` FFs the plan branch exactly as a fresh segment would (same commit ancestry). | Inheritor's commit gets its own `Guardrails-Task:` trailer at `Integrate`/`CommitStagedMerge` — identical to today. Reuse leaves no extra refs (it advances the producer's existing segment branch, deleted by `PruneStaleRunBranches`/end-sweep). |
| **ForkFromTip** | Sibling's `TaskBase = producerRecordedSha`; reset keeps producer commit. | Handle **assignment** under `_gate`; **`git worktree add` materialized off-gate** at dequeue (recommended) — no fork I/O under the gate. | Sibling forks off producer's recorded sha; first sibling to settle FFs, racers take the re-verified non-FF union — both already handled by `SettleAsync`. | New ref `guardrails/<runId>/fork/<task>/attempt-N` (`GitWorktreeProvider.cs:106`). Covered by the resume prune prefix `guardrails/<runId>/*` — **verify** `PruneStaleRunBranches` matches the `fork/` infix (it filters by `guardrails/<runId>/` prefix, `GitWorktreeProvider.cs:314` — `fork/…` is under it, so ✓). Trailer on the integrated commit unchanged. |
| **Discard** | N/A (only runs on terminal tasks). | Runs under `_gate` in `OnSettledAsync` (failure branch) or single-threaded at run end. Never touches the plan branch. | N/A. | Discard removes a segment **worktree**; the plan-branch trailers (the resume truth) are untouched. A Discarded task that *had* integrated is still on the plan branch. |
| **PruneOrphans** | N/A. | Run-end, single-threaded (after `WhenAll`). | N/A. | Prunes only registrations for already-removed trees; integration worktree reattached not pruned (08 §7). No trailer impact. |

**New failure mode introduced (named, per the brief):** *a reused/inherited directory whose producer
is Discarded while the inheritor holds it.* This is the one genuinely new hazard. It is closed
**structurally** by the `DirectoryOwner` map: ownership transfers to the inheritor on `ReuseSegment`,
and Discard only fires when the map says the *failing/terminal* task still owns the directory — and a
failing producer never has an inheritor (failure takes the block-dependents branch, which creates no
inheritor). The reuse path and the Discard path are therefore mutually exclusive on any given
directory. The test matrix pins this (T-9).

**Second new failure mode:** *ForkFromTip off a stale recorded sha.* If `Integrate` did not set
`RecordedCommitSha` before the dependent is created, the fork roots at `""`. Mitigation: the
recorded sha is captured at `GitWorktreeProvider.cs:149` inside `Integrate`, which runs inside
`SettleAsync` **before** `OnSettledAsync`'s dependent-creation block — strict happens-before. Test
T-7 asserts the sibling forks off the producer's recorded sha even after the inheritor advanced the
branch.

---

## Staged implementation plan (smallest safe increments)

Each stage is independently shippable, independently testable, and leaves the run correct if the
later stages never land.

- **M0 — Bookkeeping + no-behavior-change scaffold.** Add `RunContext.DirectoryOwner` and populate it
  on the *existing* `CreateSegment` call-sites (`Scheduler.cs:160,335`). No reuse yet. Proves the map
  is correct against today's behavior (every task owns its own fresh directory). *Ships green; zero
  behavior change.* Test: ownership map matches the fresh-per-task baseline.
- **M1 — Wire ReuseSegment + ForkFromTip.** Inheritor predicate (rule A) + sibling fork (rule B) in
  `OnSettledAsync`; deferred-fork materialization in `WorkerLoopAsync`; `TaskEnvelope.Fork`. Linear
  chains now reuse one directory; fan-outs inherit-one/fork-rest. *Independently shippable* —
  fan-in still uses the (unchanged) plan-branch union, cleanup still deferred. Tests: T-1…T-8 +
  the metamorphic count assertion (T-M).
- **M2 — Wire Discard + PruneOrphans (closes #126).** End-of-run Discard sweep + `PruneOrphans`;
  failure-path Discard; best-effort try/catch at call-sites. *Independently shippable* — pure
  cleanup, no scheduling change. Tests: T-9…T-12 + a #126 regression (no segment worktrees survive a
  green run; `_integration` does).
- **M3 — `CreateFanIn` (DEFERRED; build only if the lead overrules Decision F).** Private pre-merge
  + the non-FF re-integration contract + §5.3 SSOT paragraph + colliding-sibling re-verify wiring.
  Strictly after M1/M2 green. Tests: fan-in-via-private-merge integrates with a re-verified non-FF
  union and a correct trailer; resume-after-private-merge reconciles.

Recommended v1 ship line: **M0 + M1 + M2.** M3 is a separate decision.

---

## Test matrix (unit + integration)

Unit tests use `FakeWorktreeProvider` (distinct placeholder paths per method,
`FakeWorktreeProvider.cs:30-56`) to assert *which topology call* the scheduler made, with **no git**.
Integration tests use a real temp git repo + `GitWorktreeProvider`.

| # | Level | Behavior proven | Method |
|---|---|---|---|
| T-1 | unit | Linear A→B→C: B reuses A's directory, C reuses B's (one `ReuseSegment` per hop, zero extra `CreateSegment`). | ReuseSegment |
| T-2 | unit | Fan-out P→{D1,D2,D3} (all single-producer): exactly one inherits (longest chain; ordinal tiebreak), the other two `ForkFromTip`. | Reuse+Fork |
| T-3 | unit | Inheritor tiebreak: D with the longer `TransitiveDependentsOf` wins; equal-length → lowest ordinal id. | predicate |
| T-4 | unit | Multi-producer fan-in dependent is **never** an inheritor; it gets `CreateSegment` off the plan tip. | predicate |
| T-5 | unit | Diamond A→{B,C}→D: B,C single-producer (one reuses A, one forks); D multi-producer (fresh segment). | predicate |
| T-6 | integ | Real git: linear chain reuses one directory; the inheritor's commit FFs the plan branch; trailer present on each integrated commit. | ReuseSegment+Integrate |
| T-7 | integ | **W-2:** fork-the-rest dequeued *after* the inheritor advanced the shared branch lands on the producer's **recorded** sha, not the inheritor's tip. | ForkFromTip |
| T-8 | integ | **W-2 reset:** a retry on a reused/inherited segment resets to the producer's recorded sha; the producer's file survives, the inheritor's WIP is gone. | ReuseSegment + ResetSegment |
| **T-M** | integ | **Metamorphic — the headline.** For a linear chain of N tasks, `git worktree add` is invoked **once** (reuse path) vs **N times** (fresh-segment baseline). Assert `add`-count(reuse) < `add`-count(baseline), and == 1 for a pure chain. Instrument by counting `worktree add` (spy `GitWorktreeProvider` or count registered worktrees via `git worktree list`). | ReuseSegment |
| T-9 | unit+integ | A failed producer's directory is Discarded; its (blocked) dependents never created an inheritor — no double-free, no Discard of a held directory. | Discard + bookkeeping |
| T-10 | integ | **#126 regression:** after a fully green run, **no** `guardrails/<runId>/<task>/…` segment worktree survives; the `_integration` worktree **does** (reattached). | PruneOrphans + Discard |
| T-11 | integ | Cancellation does **not** Discard (task journaled pending; resume prune handles it). | Discard |
| T-12 | unit | Discard/PruneOrphans failures are swallowed-and-logged — a cleanup error never flips a green run off-green. | cleanup robustness |
| T-13 | integ | Fan-in **still works via the union path** (no `CreateFanIn`): fan-in task forks off a plan tip containing all producers; sees merged tree. (Locks in option (b).) | regression |
| T-M2 | integ | Crash-resume after a reused-chain hop: re-run reconstructs from plan-branch trailers; the reused directory's loss loses no integrated work. | resume + reuse |

The metamorphic test (T-M) is the brief's explicit requirement and the empirical proof that reuse
*is* the disk lever, not just a refactor.

---

## Open risks

1. **ForkFromTip lock placement.** If the deferred-at-dequeue materialization proves fiddly, the
   simple "materialize under `_gate`" fallback is correct but serializes fork I/O behind the gate
   every settling worker holds. Risk is throughput on wide fan-outs, not correctness. Decide by
   measurement in M1; the safe degrade (siblings use `CreateSegment` off the plan tip) is always
   available.
2. **`DirectoryOwner` map correctness is load-bearing.** It is the single discharge for both the
   reuse-safety proof and the Discard-exclusivity claim. A bug here re-opens the "Discard a held
   directory" hazard. Mitigation: M0 ships it with zero behavior change so it is proven against the
   baseline before any reuse rides on it. This is why M0 exists as its own stage.
3. **`PruneStaleRunBranches` vs new `fork/` refs.** `ForkFromTip` creates
   `guardrails/<runId>/fork/<task>/attempt-N` (`GitWorktreeProvider.cs:106`). I read
   `PruneStaleRunBranches` (`:312-346`) as matching by the `guardrails/<runId>/` prefix, which
   covers `fork/…`. **Verify** in M1 that the worktree-path reconstruction (`:330-331`) also handles
   the `fork` infix directory correctly; if not, an orphan fork tree could survive a `--fresh`.
4. **Reuse + the §3.4 write-scope check / F2 reset interaction.** `TaskExecutor` gates the F2 reset
   and write-scope check on `IsRealGitSegment` (`:306-310`), which requires a real `TaskBase` sha. A
   reused segment's `TaskBase` is the producer's recorded sha (real) — good. But confirm the
   write-scope diff `taskBase..segmentHEAD` is computed against the **inheritor's** `TaskBase`, not
   the producer's plan-branch base, so the inheritor's scope check sees only the inheritor's diff.
   (It does — `worktree.TaskBase` is the handle's, which `ReuseSegment` set correctly — but pin it
   with a test.)
5. **Marginal value could underwhelm.** ReuseSegment/ForkFromTip are an optimization over a correct
   baseline. If real plans are mostly wide-and-shallow (little linear depth), the disk/checkout win
   is small and the scheduling complexity may not pay. The metamorphic test quantifies the win;
   the lead should weigh it against the added `OnSettledAsync` complexity before committing M1 to
   master. (This is the honest case for "maybe just wire cleanup, skip reuse" — surfaced, not hidden.)
6. **CreateFanIn deferral is a divergence from a literal "wire the topology."** If the PO truly
   meant "wire all five," Decision F is a pushback. I recommend (b) on the merits; the lead decides.
   The cost of being wrong is low: M3 can be added later without reworking M1/M2.
