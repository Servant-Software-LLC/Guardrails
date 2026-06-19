# Architecture: Parallel execution — worktree isolation + write-scope checks + AI-merge

> **Status:** design-of-record for the parallel-execution feature. **Supersedes plan 07**
> (`07-worktree-per-task.md`), which it preserves the soundness core of and extends. This is the
> input to `/plan-breakdown` once approved. Promotes `03-roadmap.md` v2 bet #1 (worktree-per-task)
> to **active**, folds **#57 (AI merge-conflict resolution) into v1**, and records the rejected
> *shared-workspace* disjoint-scope attempt — while **salvaging its write-scope glob matcher alone,
> re-cast as a read-only CHECK** (it writes nothing; the shared-workspace enforcer's corruption class
> is structurally gone). **The matcher specification is INLINED in full in §2 of this document** (glob
> grammar, `IsInScope`/`Overlaps` semantics, the 27-row truth table, the proof-harness direction note)
> so this plan is **self-contained**: an implementer needs nothing outside this tree. Contract
> additions land in `02-schemas-and-contracts.md` (the SSOT) in the same change that implements them.
>
> **One-line claim, scoped honestly:** three mechanisms compose into one model — **worktrees
> isolate execution**; a **deterministic write-scope CHECK** keeps each task's diff small, clean,
> and test-protecting (merge hygiene + early-local-failure, only *partly* correctness); **git
> auto-merge → AI-merge → re-verify** integrates concurrent work, with the deterministic re-verify
> on the merged bytes as the verdict (never the AI, never git's no-conflict signal). The atomic
> unit is the **whole settle tail** — state-fragment merge + git integration + journal `Succeeded`
> + `mergeSequence` — committed together or rolled back together via `git reset --hard <preHead>`,
> under the serialize lock. A **harness-enforced terminal whole-repo gate** backstops every union.
> **AI auto-resolve is now a v1 feature** (high-priority, PO-revised) — but it is a *byte producer
> behind two deterministic checks*, never a verdict producer, and is **withheld at the
> user-branch boundary**.

---

## What's being asked

The product owner revised the parallel-execution vision after plan 07. Plan 07's soundness core
(physical worktree isolation; two-phase `merge --no-commit` + re-verify before commit; the unified
state+git+journal atomic settle; `reset --hard` rollback; harness-enforced terminal gate;
resume-by-trailer; honest halts) was **accepted and is preserved**. Five things change:

1. **Disjoint-scope returns — but as a deterministic read-only CHECK + a planning/scheduling aid,
   NOT the isolation mechanism.** Worktrees isolate; the write-scope check verifies that a task's
   diff stayed inside its declared surface, enforces the TDD "implementation may not write the
   tests" boundary, lets `guardrails-review` challenge an over-broad surface, and gives the
   scheduler a soft hint for filling parallel slots. It never reverts. The glob matcher it needs is
   **specified in full in §2** (this plan does not depend on any earlier scope-enforcement plan).
2. **AI-merge becomes a v1 feature** (plan 07 deferred it to v2). The PO wants *both* worktree
   isolation *and* AI-merging: git auto-merge → on conflict, an AI worker resolves → if the AI
   can't, a human. The driver is a harness that runs long without human intervention.
3. **Branch model:** a branch named after the **plan** is created off the current branch as the
   merge target; tasks run in worktrees off it. Optionally the user may run on the current branch.
4. **Worktree chaining/reuse:** root tasks fork off the plan branch; a single-upstream task forks
   off *that upstream's* worktree (reuse the tree, pass it along); a multi-upstream task forks off
   one and merges the others in; a worktree is freed when merged into its successor. "Reuse working
   trees as much as possible." A pure linear chain = ONE worktree passed along.
5. **Disk mitigations** (worktree pooling / copy-on-write / sparse checkout) the PO wants in v1,
   not v2. `maxParallelism` default rises to **3** (demo-impressive; drop to 2 only if disk bites).

Plus two narrower asks: answer the **open question** (re-run which upstream guardrails after a
multi-upstream merge?), and **reconsider the logs layout** now that `runId` exists.

This document synthesizes three exploration analyses (topology/reuse/disk; AI-merge/re-verify
soundness; disjoint-scope-as-check) into one coherent, honest design. **It is self-contained:** the
write-scope matcher (grammar, semantics, truth table, proof harness) is inlined in §2, and every
diagnostic code is assigned against the *actual current* `DiagnosticCodes.cs` (highest live code =
`GR2014`), not against any superseded plan's numbering.

### Ambiguity named & narrowed (decisions I am making, flagged where they diverge from the PO)

- **Continuous FF-integration vs the PO's literal "merge at the chain tail only."** The PO's reuse
  model implies a worktree merges into the plan branch only when its chain ends. I **diverge**: I
  integrate *every* task into the plan branch via the atomic settle — but for a **linear chain this
  is a fast-forward** (free: no union, no re-verify), so the worktree-reuse intent is fully kept
  and the plan branch becomes the single durable resume truth. This is the single most important
  decision and is flagged for the PO below (Decision 1).
- **The branch is named after the PLAN, not the run.** `runId` lives in worktree directory names
  and commit trailers, never the branch name. `runOnCurrentBranch` opt-in still integrates via a
  **harness-owned worktree on the plan branch**, never the user's live checkout.
- **The integration-guardrail set** (re-run at union points, and the terminal gate's guardrails)
  needs a precise definition and a schema seam — decided below (Decision 2; I pull a per-guardrail
  `scope:` field into v1).
- **AI-merge is withheld at the plan-branch → user-branch boundary** in v1 (Decision 3).
- **Scheduler `Overlaps` is a soft hint, v1** (Decision 4 — the most deferrable piece; I keep it,
  cheaply).
- **Logs layout** is elevated (Decision 5).
- **Re-verify default at union points = full integration set** (Decision 6).
- **`git clean -fd` (keep ignored build caches), not `-fdx`** (Decision 7).

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

| Concern | Placement |
|---|---|
| Worktree lifecycle, **reuse/chaining topology**, plan branch + per-task/segment worktrees, `IWorktreeProvider` seam | **harness** — `Execution/WorktreeManager` + `IWorktreeProvider`, `Scheduler.cs`, `TaskExecutor.cs` |
| **FF-integration** of linear hops; **non-FF union** integration of fan-ins/sibling races; the unified state+git+journal atomic settle (B1, fixed order) | **harness** — `TaskExecutor.MergeAndSettleAsync` + `AttemptJournaler`; `Scheduler` holds the serialize lock |
| `git reset --hard <preHead>` universal rollback; `reset --hard <taskBase>` + `git clean -fd` retry | **harness** — `TaskExecutor` retry loop + `MergeAndSettleAsync` |
| **Triad teardown** (delete `WorkspaceLock`, `CapturedFileStore`, `FileHashCapture`, `RestoreAncestorCaptures`, `TaskNode.Exclusive/CaptureHashes/RestoreOnRetry`, the two triad validators, the `exclusive` admission gate; retire the GR2013/GR2014 triad meanings) — **a prerequisite THIS plan delivers** | **harness + schema** — `Execution/*` deletes, `Model/TaskNode.cs`, `Loading/PlanValidator.cs`, `Loading/DiagnosticCodes.cs`; SSOT §3.1/§3.1.1 removal |
| **Terminal whole-repo integration gate** (`integrationGate: true` sink + **GR2017 missing-gate / GR2018 empty-set, both fresh**) | **harness + schema** — `TaskNode.IntegrationGate`, `PlanValidator` (GR2017/GR2018), `Scheduler` final-HEAD re-verify; SSOT §3.3 — and **skill** (`plan-breakdown` emits, `guardrails-review` flags) |
| **Write-scope CHECK** (`writeScope` task field; built-in harness check keyed on its presence; `IsInScope`/`Overlaps`/segment-matcher — **spec inlined §2**) | **harness + schema** — `Model/TaskNode.WriteScope`, new `Execution/WriteScope.cs` (matcher), `WriteScopeCheck` built-in, `PlanValidator`; SSOT §3.4 |
| **NEW attempt-decoupled re-verify seam** (`IReVerifier` — runs an integration-guardrail set against arbitrary union bytes outside an attempt lifecycle); consumed by BOTH the terminal gate and every union re-verify | **harness** — new public seam, NOT the `internal sealed GuardrailRunner` (which is attempt-lifecycle-bound) |
| **AI-merge worker** (constrained prompt action behind `IPromptRunner`; **byte producer via a NEW merge env contract on the existing on-disk file convention**; 2 deterministic checks) | **harness** — `Execution/AiMergeResolver` over the existing `IPromptRunner` + a new `GUARDRAILS_MERGE_*` env contract + a distinct merge prompt profile; SSOT §9.1 |
| **Integration-guardrail set** (`scope: "integration" \| "local"` per-guardrail) — re-run at union points via the re-verify seam | **harness + schema** — per-guardrail `scope` filter (free) on the new re-verify seam; SSOT §4.3 — and **skill** |
| **Per-guardrail `scope` emission**; **writeScope assignment**; **TDD test-exclusion**; broad-scope smell; missing-gate BLOCKER; scope-overlap WEAK | **skill** — `plan-breakdown` / `guardrails-review` |
| **NET-NEW global serialize-merges lock** (`SemaphoreSlim(1,1)` integration lock — there is no existing merge lock today; today's serializers are per-store `lock(_gate)` + the `WorkspaceLock` admission gate, which this plan DELETES) | **harness** — build the integration lock in `Scheduler` as a milestone deliverable |
| `--merge-on-success` (plan branch → user's original branch; AI-merge **withheld** here) | **harness** — `Cli` flag + `RunConfig`; end-of-run hook in `Scheduler` |
| Git-required validation gate; **all new GR codes FRESH (GR2015+)** against the actual live `DiagnosticCodes.cs` (highest live = GR2014, both taken by the triad) | **harness** — `Loading/PlanValidator.cs`, `Loading/DiagnosticCodes.cs` |
| **Logs layout** — `logs/` elevated to a sibling of `state/`, divided by `runId` | **harness + schema** — `State/` log path resolution; SSOT §1, §8 |
| **Disk v1:** chain-reuse (the big lever) + shared `.git/objects` (free) + reset-based cheap retries | **harness** — the reuse topology IS the disk lever; `maxParallelism` default **3** |
| **Worktree pooling / copy-on-write / sparse checkout** as *additional* disk mitigations | **v2 / out of scope** — honestly DEFERRED (NTFS has no reflink; the gate worktree needs the whole repo). Named in §Honest costs |
| Per-segment merge concurrency (multiple unions settling at once) | **v2** — v1 keeps ONE global serialize-merges lock |
| Re-basing the plan branch on the user's branch before delivery | **v2** — counter-6 tightening (carried from plan 07) |

---

## Invariants in play

Named, with how each is respected or strained. This design **strengthens** invariant 2 and 5 and
puts invariant 1 under the most pressure (AI-merge is a prompt in the loop) — handled by making the
AI a byte producer the deterministic re-verify gates.

1. **Deterministic guardrails over prompt-judges; judges never alone.** *Most strained, and the
   design's center of gravity.* AI-merge introduces a prompt into the integration path — exactly
   where plan 07 refused one. It is admitted **only as a byte producer, never a verdict producer**:
   the AI's output is trusted only after two **deterministic** checks (no conflict markers remain;
   blast-radius — it touched only git-reported-conflicted files), and the *pass/fail verdict* is
   the **deterministic re-verify on the merged bytes** — identical for a clean git auto-merge and
   an AI-resolved one. The judge is never alone: a deterministic gate stands between the AI's bytes
   and any green. The write-scope CHECK is likewise deterministic (a `git diff` membership test),
   never a prompt-judge. This is the #1 lesson honored: a prompt may *propose*, only a deterministic
   gate may *certify*.
2. **Harness is the single writer of merged state; children get snapshots, write fragments.**
   *Strengthened.* Each task writes only its own worktree (physical isolation — the
   concurrent-sibling-corruption class is structurally impossible). The harness, under the
   serialize-merges lock, is the single writer of the **plan branch** via git integration. The
   write-scope check **writes nothing** — it is a read-only `git diff` membership test, so the rejected
   shared-workspace enforcer's "acting for A, deleted B's files" class is gone by construction, not by
   post-hoc revert. The AI-merge worker writes only the
   fan-in's **private forked worktree** (a stable base no other merger touches), and its bytes reach
   the plan branch only through the same single-writer settle. The unified settle (B1) keeps state +
   git + journal committed-or-rolled-back as one ordered unit.
3. **Verdicts come from files, never CLI exit codes.** Respected, with the git/AI corollaries:
   git's own exit codes drive *harness control flow* (conflict detection), and the AI worker's exit
   code is ignored entirely (its bytes are checked deterministically). A *task's* pass/fail still
   comes from its guardrail outcomes re-run on merged bytes. A merge conflict the AI cannot resolve
   is a harness-level needs-human, never an uncaught throw (a git/IO failure is a verdict, not a
   throw).
4. **`02-schemas-and-contracts.md` is the schema SSOT — a contract change lands there in the SAME
   change.** Respected: the §Schema-changes block is the verbatim SSOT edit set, applied in the
   milestone that implements each piece (`writeScope` + matcher in its milestone; `scope:` field +
   AI-merge in theirs), never after.
5. **Honest halts — nothing marked done unverified; needs-human is a feature.** Respected and
   extended: an unresolvable conflict, an AI-merge that leaves markers or writes out of bounds, a
   failed re-verify (clean-auto OR AI path), a write-scope violation that exhausts retries, and a
   non-git workspace are all honest halts. The plan branch carries only the integrations that passed
   re-verify; `--merge-on-success` refuses a run the terminal gate did not certify.
6. **Plain files, light setup — no databases, daemons, or SaaS in v1.** Respected: git is the only
   added hard dependency (already universal, no daemon). The AI-merge worker reuses the existing
   `IPromptRunner` / Claude CLI seam and its **on-disk file convention** (the runner writes a file;
   the harness reads it) — **no new external dependency** — but it does need a NEW *merge* env
   contract (`GUARDRAILS_MERGE_BASE`/`_OURS`/`_THEIRS`/`_OUT`) and a distinct merge prompt profile
   layered on that convention (the existing `PromptResult` returns metadata, not bytes — §4 step 2).
   The honest tension carried from plan 07: git becomes a hard requirement, and AI-merge adds prompt
   *cost* to the integration path (named in §Honest costs).

---

## Design

### 1. Branch + worktree lifecycle (one model: isolate, reuse, integrate)

**The plan branch.** At run start the harness creates a branch named after the plan,
`<plan-name>` (e.g. `guardrails/<plan-name>` to namespace it), off the user's current HEAD, and a
**harness-owned plan/integration worktree** on it at `<root>/_integration`. This worktree is the
durable **merge target** and the **terminal-gate site**. `runId` lives in the worktree directory
names and the commit trailers, **not** the branch name (so the branch is the human-facing feature
branch the PO wants; a re-run reuses the branch and resumes onto it). A `runOnCurrentBranch` opt-in
makes the plan branch *be* the current branch — but the harness still integrates via a
harness-owned worktree on it, **never the user's live checkout**. `--merge-on-success` now means
"merge the plan branch into the original branch" (§5).

**Three kinds of worktree, all harness-owned, all under one temp root, all wiped by `--fresh` and
pruned on resume:**

1. **The plan/integration worktree** — exactly one per run, on the plan branch. Sole merge target +
   terminal-gate site.
2. **Segment worktrees** — the reused execution loci. A *segment* is a maximal linear run of tasks
   along the DAG (a chain with no branching). One segment worktree is created per active segment and
   **passed along** the chain (the reuse lever).
3. **(Transient) fan-in private worktrees** — a fresh forked worktree where a multi-upstream task's
   union is formed and (auto/AI-)merged and re-verified OFF the global lock, before its verified
   result is integrated into the plan branch under the lock (§4).

**The reuse/chaining topology (the disk lever):**

- **Root task** (no upstream): a fresh segment worktree forked off the plan branch HEAD.
- **Linear hop** (single-upstream task whose upstream has a single downstream): **reuse the
  upstream's segment worktree** — the downstream task runs *in the same tree*, on top of the
  upstream's committed tip. One worktree carries the whole chain. **This hop forms NO union** (the
  downstream builds directly on the upstream's bytes), so there is **no inter-hop merge and no
  inter-hop re-verify** — it is sound because no two independently-verified changes are being
  joined. The segment's commit FF-integrates into the plan branch at the settle (free; §3).
- **Fan-out** (a producer with ≥2 downstreams): **inherit-one** — the **longest-downstream-chain**
  successor (ordinal-id tiebreak) reuses the producer's segment worktree; **fork-the-rest** off the
  producer's **RECORDED commit sha** (the sha captured when the producer committed), **lazily at
  dequeue** (so a never-scheduled branch never pays a checkout). Each forked branch starts a new
  segment worktree. **W-2 — fork off the recorded sha, NEVER a live `rev-parse <segmentBranch>`:** the
  inherit-one successor reuses the producer's segment *branch* and may **advance its tip** before a
  fork-the-rest sibling is dequeued; if `ForkFromTip` re-derived the fork point by `rev-parse`-ing the
  live segment branch it would fork off the **inheritor's** advanced tip (wrong — it would inherit the
  inheritor's uncommitted-elsewhere work), not the producer's. The producer's tip sha is **captured at
  the moment the producer commits** and stored on the `WorktreeHandle`; `ForkFromTip` uses that stored
  sha. (Stage-2 test: fork-the-rest dequeued *after* the inherit-one successor has committed lands on
  the producer's tip, not the inheritor's.)
- **Fan-in** (a task with ≥2 upstreams): **fork one upstream's worktree** (the inherited segment)
  and **merge the others in** (git auto-merge → AI-merge → re-verify; §4). This is a real **union**.
- **Free a worktree** when its segment is merged into its successor (the inherit-one path consumes
  it) or when the chain tail FF-integrates into the plan branch and has no further downstream.

**Worktree count peaks at the DAG's maximum antichain width** (the most tasks runnable in parallel
at once), capped by `maxParallelism` — **NOT 1**, and I state this honestly: a pure linear chain is
one worktree, but a wide fan-out at `maxParallelism: 3` is up to 3 segment worktrees + the
integration worktree + any transient fan-in tree. Reuse minimizes *creation churn* (and the per-fork
checkout cost), not the peak count.

**Retries preserve upstream work by construction.** A failed attempt does **not** discard-and-recreate
the worktree (plan 07's model). Instead the harness `git reset --hard <taskBase>` + `git clean -fd`
(Decision 7). **`taskBase` is defined precisely as the segment-worktree commit sha captured at the
moment the worktree was (re)assigned to THIS task attempt** — recorded in the `WorktreeHandle`, never
re-derived from "HEAD's parent" (which would be wrong after a commit-on-top or an inherited-chain FF
advanced the tip). It is the post-ancestor commit for a root/linear task, or the post-fan-in union
commit for a fan-in task. `taskBase` is **distinct from the plan-branch `preHead`** (the integration
rollback point); **conflating them is the corruption bug** (it would reset across upstream commits and
lose work — a Stage-2 test gate). The reset discards *only this task's* working changes while keeping
every upstream/sibling commit that is an *ancestor* of `taskBase`.

**Two invariants make reset-retry safe (closing the "reset nukes a sibling's merged work" hazard):**
1. **A segment worktree's branch is single-task-at-a-time and NEVER a fan-in target while a task on it
   is live-and-retryable.** No other task may commit onto, or fan-in-merge into, a segment branch
   while the task occupying it can still retry. (The scheduler enforces this: a segment branch is
   owned by exactly one live task; fan-ins fork a *private* worktree and merge the others in there,
   never into a sibling's live segment; non-FF integration happens in the *integration* worktree, not
   a segment.) Therefore no sibling commit can ever fall in the `taskBase..HEAD` range a reset
   discards — the #88 cross-task-corruption vector is structurally absent.
2. **`taskBase` is captured at assignment, not inferred** (above) — so a fan-out-inherits-one step
   that advanced the inherited chain's tip cannot leave `taskBase` pointing behind the true fork point
   (data loss) or ahead of it (a stale-attempt false-green).

(The reset-based retry is also the real Windows disk win: no re-checkout per attempt — see §Honest
costs. **Honest residual (d3):** `clean -fd` removes untracked files so a retry does not inherit a
prior attempt's stray *untracked* output, but Decision 7 keeps git-*ignored* build caches — so a
retry is pristine on tracked + untracked source, with build caches deliberately warm; the Stage-2
stale-artifact test pins that the warm cache cannot false-green.)

```csharp
public interface IWorktreeProvider
{
    // Run start: create the plan branch <plan-name> off the user's HEAD + the integration worktree.
    IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct);

    // Root: fork a fresh segment worktree off the plan-branch HEAD.
    WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct);

    // Linear hop / fan-out inherit: reuse an existing segment worktree, advancing it to the task.
    WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt);

    // Fan-out fork-the-rest: fork a NEW segment off the producer's RECORDED commit sha (captured at
    // producer-commit time; NEVER a live rev-parse of the segment branch, which the inheritor may have
    // advanced — W-2), lazy at dequeue.
    WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt);

    // Fan-in: fork a PRIVATE worktree off one upstream and prepare to merge the others in (§4).
    FanInHandle CreateFanIn(WorktreeHandle chosenUpstream, IReadOnlyList<WorktreeHandle> others,
                            string taskId, int attempt, CancellationToken ct);

    // Settle a verified segment/union commit into the plan branch: FF when linear (free), else a
    // real (non-FF) merge that the caller has ALREADY re-verified (§3). Under the serialize lock.
    IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct);

    void Discard(WorktreeHandle handle);                                  // git worktree remove --force; idempotent
    void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ);
    MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct); // §5
}
```

`WorktreeHandle` carries the worktree path, its segment branch name
(`guardrails/<runId>/<segmentId>`, with a per-attempt suffix `…/<segmentId>/attempt-N` so retries
never collide — and the `--fresh`/prune `branch -D guardrails/<runId>/*` glob matches all of them,
N-1), its current `taskBase` commit, the **recorded commit sha captured when this segment last
committed** (the fork point a fan-out fork-the-rest uses — W-2, never a live `rev-parse`), and the
plan-branch HEAD it descends from. `IntegrationHandle` carries the
integration worktree path, the plan-branch name, the user's **original** branch + HEAD sha (captured
at run start, load-bearing for `--merge-on-success` and divergence detection), and the run id.

### 2. Write-scope CHECK (deterministic, read-only — disjoint-scope reborn as a guardrail)

`writeScope` returns as a **per-task declared write surface** that the harness verifies with a
**deterministic, read-only check**. It is **not** the isolation mechanism (worktrees are) and it
**never reverts** (the rejected shared-workspace enforcer's irreversible-corruption class is gone by
construction — there is no enforcer, only a read-only diff membership test).

**The check (a harness built-in, sibling of the §3.3 integration-gate check):**

- Keyed on `task.json.writeScope` **presence**. **Absent ⇒ no check** (the off-switch — a task
  `plan-breakdown` can't confidently scope omits the field; it is reported as a "broad surface", not
  given a vacuous `**`).
- Runs **AFTER the action, BEFORE the task's own `guardrails/`** (cheapest-first; it is a pure diff
  membership test). The declared scope is **also injected into the action prompt** (advisory) — but
  the deterministic check is the gate, not the prompt.
- Computes `git diff --name-status <taskBase>..<segmentHEAD>` in the task's segment worktree (the
  task's own contribution, isolated from upstream commits by the `taskBase` anchor). **Every** A/M/D
  path must satisfy `IsInScope(path, writeScope)`. A violation ⇒ **guardrail-failure** → retry with
  feedback naming the out-of-scope paths → eventual needs-human. **Deletions:** the deleted path
  must be in scope. **Renames:** the check does **NOT** use `git`'s `-M` rename detection; a rename
  presents as a paired **D + A**, and **both** the old and new path must be in scope.

#### 2.1 The write-scope matcher — FULL specification (inlined; this plan depends on nothing else)

The matcher (`Execution/WriteScope.cs`) is built **fresh** to this specification. (A glob matcher
existed in the rejected shared-workspace branch and may be lifted as a *starting point*, but it
carried a known permissive bug — described under "bug direction" below — and that branch is **not in
this tree**, so the spec here is authoritative and the implementation is verified against the truth
table + fuzz harness below, not against any prior code.)

**(a) Glob grammar — segment globs.** A scope entry is a `/`-separated sequence of **segments**.
Matching is segment-by-segment against the `/`-separated path. Within a single segment:

- A **literal** segment matches that exact segment (comparison `OrdinalIgnoreCase` on Windows,
  `Ordinal` elsewhere — one `SegmentComparison` constant, used everywhere).
- A segment may contain one or more **`*`** wildcards. A `*` matches **zero or more** characters
  **within that one segment** (it never crosses a `/`). The segment's **literal prefix** (before the
  first `*`), **literal suffix** (after the last `*`), and **every literal run between two `*`s** are
  all load-bearing — the matched segment must start with the prefix, end with the suffix, and contain
  each interior literal **in order**. `Feat*` matches `Feat`, `Feature`, `FeatX` but not `OtherFeat`;
  `*Tests` matches `UnitTests` but not `UnitTestsExtra`; `*-*.cs` matches `foo-bar.cs` (consuming
  `foo` `-` `bar` `.cs`) but not `foobar.cs` (no literal `-`).
- A segment that is exactly **`**`** matches **zero or more whole segments** (it crosses `/`). It may
  appear leading (`**/*.cs`), mid-pattern bounded by literals (`a/**/b/**` — the literal `b` segment
  must reappear between the two `**`s), or trailing (`src/Feature/**`).
- A **bare directory** entry (no glob, e.g. `src/Feature`) **normalizes to `src/Feature/**`** — it
  matches files *under* the directory, never the directory entry itself.
- An **empty** scope list `[]` matches **nothing** (a task that owns nothing); `["**"]` matches
  **everything** (universal — disables the check's discriminating power; a granularity smell, GR2020).

**(b) `IsInScope(path, scope)` semantics.** True iff **any** entry in `scope` matches `path` under the
grammar above. This is the **membership** primitive the CHECK uses: every A/M/D path in the task's
diff must be `IsInScope`. It is read-only — it inspects a path string, it writes nothing.

**(c) `Overlaps(scopeA, scopeB)` semantics.** True iff there **could exist** a path `p` such that
`IsInScope(p, scopeA)` **and** `IsInScope(p, scopeB)` — i.e. the two declared surfaces share at least
one witness path. This is the **scheduler hint** primitive (§6) and the `guardrails-review`
overlap probe. It is computed structurally (segment-by-segment compatibility, `**` absorbing any
remaining segments), **not** by enumerating paths. `Overlaps` must be **complete** (no false
negatives — see the second fuzz property in 2.2): two genuinely-overlapping scopes must never report
`false`, or the scheduler hint silently puts a colliding pair in parallel.

**(d) The 27-row truth table** (`IsInScope`; `*` honors prefix/suffix within a segment, `**` spans
any depth, comparison per `SegmentComparison`). These rows are the **regression floor** the
implementation MUST satisfy and the test suite MUST pin verbatim:

| # | Scope glob | Path | `IsInScope` | Why |
|---|---|---|---|---|
| 1 | `src/Feat*/**` | `src/OtherDir/Z.cs` | **false** | literal prefix `Feat` must match the segment start (the permissive-bug row) |
| 2 | `marks/left*` | `marks/right.start` | **false** | `left` prefix; the membership/overlap divergence case |
| 3 | `marks/left*` | `marks/left.start` | **true** | prefix matches |
| 4 | `src/**/*.cs` | `src/x/secrets.json` | **false** | final `*.cs` segment must end in `.cs` |
| 5 | `src/**/*.cs` | `src/x/y/Thing.cs` | **true** | `**` spans `x/y`, final segment matches `*.cs` |
| 6 | `src/**/*.cs` | `Foo.txt` | **false** | not under `src/`, wrong extension |
| 7 | `src/Feature/**` | `src/FeatureX/Z.cs` | **false** | sibling-prefix trap — `FeatureX ≠ Feature` as a whole segment |
| 8 | `src/Feature/**` | `src/Feature/a/b.cs` | **true** | `**` spans `a` |
| 9 | `src/Feature` (bare) | `src/Feature/x.cs` | **true** | bare dir normalizes to `src/Feature/**` |
| 10 | `src/Feature` (bare) | `src/Feature` | **false** | a directory scope matches files *under* it, not the dir entry itself |
| 11 | `[]` (empty) | *anything* | **false** | empty scope owns nothing |
| 12 | `["**"]` (universal) | *anything* | **true** | universal owns everything |
| 13 | `src/A/*` | `src/A/B/c.cs` | **false** | single `*` is one level only |
| 14 | `src/A/*` | `src/A/b.cs` | **true** | one level matches |
| 15 | `src/*/Tests/**` | `src/Foo/Tests/X.cs` | **true** | `*` matches `Foo`, `**` spans the rest |
| 16 | `*.md` | `README.md` | **true** | top-level extension glob |
| 17 | `*.md` | `docs/README.md` | **false** | `*.md` is one segment; not under `docs/` |
| 18 | `src/*Tests/**` | `src/UnitTests/X.cs` | **true** | **literal SUFFIX after `*`** — segment must END in `Tests` |
| 19 | `src/*Tests/**` | `src/UnitTestsExtra/X.cs` | **false** | suffix `Tests` must be the segment END; `UnitTestsExtra` ends in `Extra` |
| 20 | `src/*-*.cs` | `src/foo-bar.cs` | **true** | **multiple `*` in one segment** — both consumed (`foo` `-` `bar` `.cs`) |
| 21 | `src/*-*.cs` | `src/foobar.cs` | **false** | the literal `-` between the two `*`s must appear; `foobar` has none |
| 22 | `a/**/b/**` | `a/x/b/y.cs` | **true** | **bounded mid-`**`** — first `**` spans `x`, literal `b` reappears, second `**` spans `y` |
| 23 | `a/**/b/**` | `a/x/c/y.cs` | **false** | bounded mid-`**`: no `b` segment between the two `**`s |
| 24 | `**/*.cs` | `src/x/Thing.cs` | **true** | **leading `**/`** — `**` spans `src/x`, final segment matches `*.cs` |
| 25 | `**/*.cs` | `src/x/Thing.txt` | **false** | leading `**/`: depth spans, but final segment is not `*.cs` |
| 26 | `src/Feat*/**` | `src/Feat/x.cs` | **true** | **`*` matches EMPTY** — `Feat*` matches the bare segment `Feat`, then `**` spans `x` |
| 27 | `src/Feat*` | `src/Feat` | **true** | `*`-matches-empty at the leaf: `Feat*` matches exactly `Feat` (contrast row 10's bare-dir rule) |

**(e) The bug direction — why the proof harness must tests-FAIL-on-the-naive-matcher FIRST.** The
dangerous failure mode of a segment matcher is a **naive implementation that, on any `*`-bearing
segment, discards the segment's literal prefix/suffix (and any second `*`) and just accepts the
segment** (the classic "if the segment contains `*`, skip to the next segment" shortcut). That bug is
**PERMISSIVE**, not conservative: `IsInScope` returns `true` for paths it should reject (rows 1, 4, 7,
18→true-wrongly becomes the inverse, 19, 21, 23, 25 are the traps). For the write-scope CHECK that is
the **worst** direction: a permissive matcher **MISSES catches** — it passes a real out-of-scope write
**green**, a green check standing over an actual escape (e.g. an implementation task silently editing
the test files its scope was supposed to exclude). "False-red" badly undersells the risk; the honest
bound is **"one task's own false-red OR missed-catch."** Therefore the fuzz/property gate **MUST be
proven to FAIL against a deliberately-naive permissive matcher BEFORE the real matcher is written**
(tests-fail-on-current-code): a fuzz suite that passes against the naive matcher proves nothing. The
fix is **not** a one-liner — suffix-after-`*`, multiple-`*`-per-segment, and bounded mid-`**` all
require real segment-glob parsing, not a skip.

**(f) Base coincidence — no `workingDirectory` rebasing.** The diff is taken at the
**segment-worktree root** with scope globs relative to it (worktree root = workspace root for that
tree), so diff paths and scope globs share a base. The shared-workspace plan needed a
`workingDirectory`-rejection validation to keep the bases aligned; the worktree model makes the bases
coincide **by construction**, so that machinery is **not** carried here.

**Matcher blast-radius downgrade (stated ONCE, here, at the point of claim).** Because the CHECK
**writes nothing**, a matcher bug on the CHECK's verdict path is confined to **one task's own verdict**
(a false-red blocks a legitimate task; a missed-catch passes that task's own out-of-scope edit) —
**never** the rejected shared-workspace enforcer's irreversible cross-task corruption (there is no
enforcer to corrupt a sibling). **This downgrade applies to `IsInScope`-the-CHECK only.** It does
**NOT** apply to `Overlaps`-the-scheduling-hint: an under-detecting `Overlaps` can put two genuinely
colliding tasks in parallel, whose later union, if AI-resolved by dropping a hunk, can compose into a
broken integration (§4, and the §DA self-critique). So `Overlaps` **retains cross-task reach** and
therefore keeps the **full fuzz rigor** (both properties in 2.2), even though the CHECK's verdict path
is downgraded. The reader does not have to find this qualification 500 lines later — it is stated at
the claim.

#### 2.2 The proof harness (a milestone gate, not a suggestion)

**Do NOT ship the matcher without BOTH:** (i) the 27-row table above pinned as data-driven tests;
(ii) **two** generative fuzz/property tests over a glob+path grammar covering the rows-18-27 shapes
(literal prefix/suffix, multiple `*`, bounded `**`, leading `**/`, `*`-matches-empty), seeded and
reproducible so a counterexample replays:

1. **Membership-implies-overlap.** For any non-empty scope `S` and path `p`: if `IsInScope(p, S)`
   then `Overlaps(S, Parse([p as a literal-glob]))`. (Catches the historical contradiction where
   `Overlaps(['marks/left*'],['marks/right*']) = false` while
   `IsInScope('marks/right.start', ['marks/left*']) = true`.)
2. **`Overlaps` completeness (no false negatives) — NEW, and load-bearing for the scheduler hint.**
   The generator produces scope **pairs** that share a constructed **witness path** `w`
   (`IsInScope(w, A) ∧ IsInScope(w, B)` by construction); the property asserts `Overlaps(A, B)` is
   **true** for every such pair. Membership-implies-overlap does **not** constrain this — it bounds
   `Overlaps` from below given membership, but says nothing about a pair that shares a witness neither
   side's *declared literal* exposes; an `Overlaps` that under-detects there is exactly the bug that
   feeds the scheduler-hint cross-task hazard (§DA-5). This property is the one whose failure is
   load-bearing, so it is mandatory, not optional.

Both callers (`IsInScope` and the membership half of `Overlaps`) derive from **one** shared
segment-match helper (DRY on a primitive with cross-task reach); the fuzz tests are what catch a
future drift if they ever stop sharing it.

**The TDD test-protection replacement.** `plan-breakdown` assigns each task a `writeScope` from its
primary artifact(s). For a TDD pair, the **test-author task owns the test files**, and the
**implementation task's scope EXCLUDES the test files**. The write-scope check then deterministically
enforces "the implementation may not write the tests" — this is **THIS plan's replacement** for the
`captureHashes`/`tests-untouched`/`restoreOnRetry` triad, **which is still fully present on `master`
and which this plan tears down** (`TaskNode.CaptureHashes`/`RestoreOnRetry`/`Exclusive`,
`CapturedFileStore`, `FileHashCapture`, `RestoreAncestorCaptures`, two validators — see the triad
teardown line items in §Milestones). The replacement is a deterministic diff membership test: no
hashing, no restore, no shared git-index race.

**What the check is and is NOT (honest framing).** Write-scope-as-check is mostly **merge-hygiene +
planning discipline + test-protection**, only **partly correctness** — worktree isolation + the
merge-back re-verify already catch *build-breaking* escapes. Its distinct value: it converts a late,
tangled fan-in conflict into an **early, local, single-task honest failure**, which is exactly what
keeps fan-in merges small and AI-merge tractable (the PO's insight — see §6). Its residual: scopes
can be **theater** if declared too loose; the `guardrails-review` probes are the (imperfect)
mitigation. **Precise about what is closed and what is not** (reconciling §4's framings, W-3): the
blast-radius check (§4) mechanically closes the **cross-file** clobber (the AI writing a file outside
the conflicted set); the **in-conflicted-file** hunk drop is NOT mechanically closed — it is bounded
only by the colliding-sibling re-verify (B-3, §4 step 3) plus the terminal gate. Nowhere in this plan
should "mechanically closed" be read as closing the in-file drop.

### 3. Integration: FF for linear, re-verified union for fan-in/sibling races (the atomic settle)

There are exactly two kinds of **union point** — places where two independently-verified changes
are joined and re-verify is mandatory: **fan-ins** (§4) and **non-FF plan-branch integrations**
(a sibling settled into the plan branch since this segment forked). **Linear hops form no union;
FF integrations form no union** → neither re-verifies.

**FF-integration (linear chain → plan branch, free).** When a segment's commit can fast-forward the
plan branch (no sibling has advanced it since the segment forked), `git merge --ff-only` introduces
**no new merged bytes** — the plan-branch tip simply moves to the already-verified segment commit.
There is **nothing new to re-verify** (the bytes already passed the task's own guardrails in the
segment worktree). This is the headline efficiency: a linear chain of N tasks produces N free
FF-integrations, zero re-verifies at integration time (the terminal gate still runs once at the end).

**Honest soundness regression vs plan 07 (named, not hidden — and the bound is CONDITIONAL).** Plan
07 re-verified at *every* merge-back, including each linear hop. This design re-verifies a linear
chain only at the **terminal gate**, not per hop — so a chain A→B→C where C's commit-on-top is
*jointly inconsistent* with A's earlier work (a clean, non-conflicting but semantically broken
sequence) is caught at the terminal gate, **not** at C's integration. The justification: a linear hop
forms **no union** — C builds *directly on* B's bytes (it is not joining two independently-verified
branches), so C's own guardrails ran against a tree that already contains A+B. The residual is the
same class as a single task with weak guardrails (C's guardrails may not exercise the A-interaction).

**The "no wider than plan 07's counter-1" claim is CONDITIONAL — state it honestly.** Plan 07
re-verified the integration set at **every hop** (N chances to catch a cross-hop interaction break);
plan 08 has **one** chance — the terminal gate. So "no wider than plan 07's residual" holds **only if
the terminal gate's integration set actually exercises the cross-hop interactions** that plan 07's
per-hop re-verifies would have caught. A weak or thin terminal gate makes plan 08's residual **wider**
than plan 07's, not equal. Therefore this plan makes a **missing or thin terminal integration gate a
HARD validation ERROR** (fresh GR codes — see §Schema-changes), not advisory: a multi-leaf or
fan-in-bearing plan with no `integrationGate` sink fails `validate` (**GR2017**), and a plan whose
`integrationGate` sink carries no `scope: "integration"` guardrail fails `validate` (**GR2018**, the
empty-integration-set case). The trade — N free FF-integrations vs N per-hop re-verifies
— is deliberate and is a real reduction in *per-hop* integration verification, paid for by a terminal
gate that validation now FORCES to exist and to be non-empty. Flagged so it is not mistaken for a
pure win, and so the conditional is not buried.

**Non-FF integration (a sibling raced us).** If a sibling settled into the plan branch since this
segment forked, the FF is impossible and a **real (non-FF) merge** forms a union of this segment's
commit with the sibling's — which **must be re-verified on the merged bytes before the commit**,
exactly like a fan-in. The settle for this case follows the union path (§4 re-verify + §3 atomic
tail).

**The serialize-merges lock is NET-NEW — build it first (feasibility fix 3).** There is no existing
"merge lock" to wire against. Today's serializers are the per-store `lock(_gate)` inside
`StateManager` and `RunJournal`, plus the `WorkspaceLock` admission gate — and **this plan DELETES
`WorkspaceLock`** (it gated the `exclusive` triad field, also deleted). So the global
serialize-merges lock is a **new `SemaphoreSlim(1,1)` integration lock**, owned by the `Scheduler`,
built as a milestone deliverable (M4). The "off-lock / under-lock" discipline below presumes that lock
**exists**; it does not "wire against the real lock" because the real lock does not exist yet — it is
constructed here.

**The atomic settle (preserved verbatim from plan 07 — B1, fixed order).** Every integration
(FF or non-FF) commits its effects as **one ordered unit under the [new] serialize-merges lock**, or
rolls back as one. The fixed success order is **(1) state-fragment merge → (2) git integration commit
→ (3) journal `Succeeded` + consume `mergeSequence`**. The two-phase `git merge --no-commit` produces
the merged bytes (so re-verify runs on exactly the bytes that ship) **before** the authoritative
commit exists; the resume authority (the commit + its trailer) is created only at step 2.

```
acquire serialize-merges lock                      # one integration at a time, into the plan branch
  preHead = git -C <integ> rev-parse HEAD          # rollback point ON THE PLAN BRANCH
  try FF:   git -C <integ> merge --ff-only <segmentCommit>
  if FF succeeded:
     # no new union, no re-verify — the bytes already passed the task's guardrails in the segment
     (1) write fragment -> (2) [commit IS the FF move] -> (3) journal Succeeded + mergeSequence
     SUCCEED; release lock
  else:   # a sibling advanced the plan branch — REAL union, must re-verify on merged bytes
     result = git -C <integ> merge --no-commit --no-ff <segmentCommit>
     if result == CONFLICT:  -> AI-merge attempt (§4); if unresolved -> reset --hard preHead; needs-human
     # via IReVerifier on the merged bytes — B-3: colliding siblings' FULL sets run UNCONDITIONALLY:
     reRun = reverify( this task's full guardrails
                       + EVERY colliding sibling's FULL guardrails (no touched-files skip)
                       + the run's INTEGRATION-guardrail set ) on the merged bytes
     if NOT reRun.AllPassed:  git -C <integ> reset --hard preHead; needs-human; no fragment; no sequence
     assert porcelain shows only the staged merge (W3 read-only check); else reset --hard preHead; needs-human
     (1) write fragment -> (2) git commit (merge commit + Guardrails-Task: trailer) -> (3) journal + sequence
     SUCCEED; release lock
release lock
```

**`git reset --hard <preHead>` is the universal rollback** on every non-success path — NOT `merge
--abort` (which fails rc=128 "Entry not uptodate" on a re-verify-dirtied tracked path). Any non-zero
from the rollback itself ⇒ needs-human (not a throw). The settle tail (state + git + journal) is
**not truly atomic across three stores** — it is *serialized + ordered + idempotent-on-replay*, with
one self-healing transient (a fragment can briefly exist for a not-yet-committed task — own
namespace, producer re-runs, no consumer sees it). The load-bearing ordering choice
(fragment **before** the git commit) is preserved exactly from plan 07; reversing it re-introduces
the B1 split-brain.

### 4. AI-merge + the deterministic re-verify (the v1 union mechanism)

A union is formed at a **fan-in** (fork one upstream, merge the others in) or a **non-FF plan-branch
integration** (§3). The merge proceeds **git auto-merge → AI-merge → human**, with the deterministic
re-verify as the verdict in all cases.

**Step 1 — git auto-merge.** `git merge --no-commit` (in the fan-in's private worktree, or the
integration worktree for a non-FF settle). A clean auto-merge skips straight to re-verify.

**Step 2 — AI-merge (only on conflict).** The **AI-merge worker is a constrained prompt action behind
`IPromptRunner` — a BYTE PRODUCER, never a VERDICT PRODUCER.** **It is NOT "the existing seam
returning bytes"** — the real `IPromptRunner.RunAsync(PromptInvocation, ct) → PromptResult` takes a
**composed STRING prompt over stdin** and returns **metadata only** (`{Completed, IsError, ResultText,
CostUsd, NumTurns, Summary}`); there is **no byte channel**. The existing convention is "the runner
writes a file on disk (e.g. `GUARDRAILS_VERDICT_OUT`), the harness reads it." So the AI-merge worker
is built as **a NEW merge env contract on that existing on-disk file convention**, plus **a distinctly
named merge prompt profile** (NOT `guardrailOverrides`, which is a guardrail-prompt concept — see
N-4):
- **A new merge env contract** (SSOT §9.1): `GUARDRAILS_MERGE_BASE`, `GUARDRAILS_MERGE_OURS`,
  `GUARDRAILS_MERGE_THEIRS` point the worker at the three-way inputs on disk; `GUARDRAILS_MERGE_OUT`
  is the path the worker writes the resolution to. The harness composes the prompt (intents + the
  conflicted file list) as a string, the worker writes the resolved bytes to `_OUT`, the harness reads
  `_OUT` — same disk-file convention as verdict files, reusing `IPromptRunner` unchanged.
- **Given:** the conflicted files (with conflict markers) + base/ours/theirs on disk, and the
  colliding upstream tasks' *intents* (their `task.description` + declared `writeScope`) in the prompt.
- **Returns (via `_OUT` on disk):** the **merged bytes only**. A rationale is written to the attempt
  log (NON-gating — never read as a verdict). `PromptResult.IsError`/exit code are **not** the verdict.
- **Trusted via two DETERMINISTIC checks:** (i) **no conflict markers remain** (`git diff --check`);
  (ii) **blast-radius** — the AI touched **ONLY** the git-reported-conflicted files
  (`git status --porcelain` shows no modification outside the conflicted set). **An out-of-bounds
  write ⇒ discard the AI's bytes (`reset --hard`) + needs-human.** This mechanically closes the
  rejected shared-workspace design's **cross-file** clobber ("the AI clobbered a sibling's file
  *outside* the conflict"): a write outside the conflicted set is *detected and rejected* before any
  re-verify. It does **NOT** close the **in-conflicted-file hunk drop** — that is closed only by the
  colliding-sibling re-verify in step 3 (B-3) + the terminal gate.
- **A distinct merge prompt profile** (NOT `guardrailOverrides`): a reserved runner profile (e.g.
  `ai-merge`) under `promptRunners` with a read-the-conflict/write-only-`_OUT` tightening — named for
  what it is, a merge profile, not a guardrail-verifier profile.
- **Bounded budget; 1 retry (2 attempts total).** Escalate to **needs-human** on markers-left,
  out-of-bounds, re-verify-fail, or budget exhaustion.

**Step 3 — the verdict is the deterministic re-verify, IDENTICAL machinery for clean-auto and
AI-resolved paths.** Re-run, on the `--no-commit` merged bytes — **BEFORE the downstream task starts
its action and BEFORE the merge commit** — exactly these three sets:

1. **The union task's OWN full guardrail set** (every guardrail, `local` and `integration`).
2. **EVERY colliding sibling's OWN FULL guardrail set — UNCONDITIONALLY, with NO touched-files
   filter.** This is the B-3 fix and it is load-bearing: the whole reason to re-run a colliding
   sibling's guardrails is that the AI may have **DELETED the sibling's contribution** while resolving
   the conflict. If the AI dropped the sibling's *source* hunk, the sibling's *test* file is
   untouched by the merge — so a touched-files local-skip would see nothing changed and **SKIP the
   sibling's `local` guardrail at exactly the union where it is needed**, re-opening the hole. The
   colliding siblings' guardrails therefore run **in full at every union they participate in**; the
   touched-files local-skip is **NEVER** applied to a colliding sibling.
3. **The run's integration-guardrail set** (§4a) — always.

**The touched-files local-skip applies ONLY to distant, NON-colliding tasks.** A task that is neither
the union task nor a colliding sibling has its `local` guardrails re-run at this union **only if the
merge touched that task's files**; its `integration` guardrails are covered by set 3 regardless. This
is the only place the optimization is sound, because a distant non-colliding task's contribution was,
by definition, not part of this merge's conflict set, so the AI could not have dropped it here.

**This is the answer to the PO's open question (#7):** the union task's full set + every colliding
sibling's full set (unconditional) + the run's integration-guardrail set — NOT all-transitive
guardrails, and NOT a touched-files filter on the colliding siblings. A distant invariant a merge
*can* break is, by definition, an **integration (cross-cutting)** property (covered by set 3), while a
purely-local **distant** invariant cannot be broken by a merge that did not touch its files (the
local-skip, distant-only). Decision 6: the default integration set is the full one, not a
build-level-only opt-in.

**Internal consistency check (B-3):** the hardening (re-run colliding siblings) and the optimization
(local-skip) **do not contradict** because they apply to **disjoint** task sets — colliding siblings
get the full unconditional set; only distant non-colliding tasks get the touched-files skip. There is
no task to which both rules apply, so there is no union at which a colliding sibling's `local`
guardrail is skipped.

**Why the colliding siblings' guardrails, not just the merging task's (closing the AI-deleted-hunk
hole).** The two deterministic checks (no markers; blast-radius) are both satisfiable by the AI
**deleting a hunk** instead of resolving it — emit only ours, only theirs, or an empty hunk: no
markers remain (check i passes), only the conflicted file was touched (check ii passes), the bytes
compile. If re-verify ran *only the merging task's* guardrails and the AI kept the merging task's
side, the merging task's guardrails pass while the discarded sibling's change is silently gone —
**this is exactly the shared-workspace design's "clobbered-sibling-hunk" hole (the merging task's
guardrails do not cover the dropped sibling's content), which plan 07 made MOOT by banning AI
auto-resolve.** Reviving AI-merge revives that hole UNLESS the re-verify also runs **the colliding
sibling's own guardrails, in full and unconditionally** (step 3, set 2) — the only party whose
guardrails assert the dropped content, and the reason the touched-files skip must never apply to a
colliding sibling (the dropped hunk leaves the sibling's *test* file untouched, so a touched-files
filter would skip exactly the guardrail that catches the drop). So the union re-verify set is the
merging task's full guardrails ∪ every colliding sibling's full guardrails ∪ the integration-guardrail
set. **This still does not catch a *semantic* conflict in cleanly-merged files** (a sibling renamed a
symbol the merging task calls, in a file with no textual conflict) — that is caught only if the
relevant guardrail is integration-scoped, which is why **the terminal whole-repo gate (§3.3) is
LOAD-BEARING here, not a mere backstop:** AI-merge's per-union re-verify provides *no* semantic-conflict
protection beyond what these guardrail sets already give; the terminal gate is the union's whole-repo
soundness boundary. Stated, not oversold.

**Lock discipline (this is what keeps AI-merge tractable concurrently).** The AI-merge + its
re-verify run **OFF the global serialize-merges lock**, in the fan-in's **private forked worktree** —
a stable base no other merger touches, so the (slow, prompt-driven) AI work and the (slow,
full-suite) re-verify do not block other integrations. **ONLY** the integration of the *verified
result* into the shared plan branch is **under the global lock**, with a **re-verify against the
CURRENT plan-branch bytes** (catches staleness from a concurrent sibling that settled while this
union was being resolved). The B1 atomic settle on the plan branch stays under the lock.

**AI-merge is used for all harness-internal unions** (fan-in + non-FF plan-branch integration). It
is **WITHHELD at the plan-branch → user-branch (`--merge-on-success`) boundary** in v1 (Decision 3):
the user's mid-run commits are un-gated bytes the harness never integration-tested, so a conflict
there ⇒ **needs-human, plan branch intact** — never an AI auto-resolve of the user's own work.

#### 4a. The integration-guardrail set — ONE coherent "integration" notion

Three "integration" notions must reconcile into one thing:
1. plan 07's `integrationGate` **sink task** — the *terminal* whole-repo gate on the final HEAD;
2. the **integration-guardrail set** re-run at *every* union point (§4 step 3);
3. how `writeScope.Overlaps` *predicts* which tasks will form unions (§6, scheduler hint).

**Definition (Decision 2 — I pull the per-guardrail field into v1):** a guardrail declares
`scope: "integration" | "local"` (default `"local"`). The **integration-guardrail set** of a run =
the union of all guardrails declared `scope: "integration"` across the plan (typically: the
whole-repo build, the whole-suite tests). This set is what re-runs at **every union point** (§4 step
3) and is exactly the guardrails the **terminal `integrationGate` sink** carries on the final HEAD.
**The terminal gate is the same mechanism at the final scope:** the `integrationGate: true` sink's
guardrails ARE the integration-guardrail set, run once on the whole merged union; §4 step 3 runs that
same set at each intermediate union.

**This is NOT "no new machinery" — be precise (feasibility fix 2).** The per-guardrail `scope`
**filter** is free (a predicate over the guardrail list). But the **invocation** is not: today's
`GuardrailRunner` is `internal sealed`, **executor-private and attempt-lifecycle-bound** — it requires
an attempt `logDir`, a per-attempt state snapshot, and the `GUARDRAILS_ACTION_STDOUT/STDERR/RESULT`
env produced by *the action that ran in this attempt*. Running an integration-guardrail set against
**arbitrary UNION bytes mid-DAG** — where no action ran, there is no attempt number, and there is no
action result — needs a **NEW PUBLIC, ATTEMPT-DECOUPLED re-verify seam** (call it `IReVerifier`):
given a worktree path + a guardrail set, run those guardrails against the bytes on disk and return a
pass/fail, with **no** dependence on an attempt lifecycle or an action result. This seam is a
**first-class milestone deliverable** — **BOTH** the terminal gate AND every per-union re-verify
depend on it; it does **not** fall out of relocating the settle. The `scope` filter is the cheap part;
the attempt-decoupled invocation on arbitrary bytes is the part that must be built.

**Why a per-guardrail field and not "reuse the integrationGate task's commands" (the rejected
v1-A).** The explorer leaned toward reusing the sink task's declared guardrail commands at union
points (no new schema field). I reject it for v1 because a **sink task's guardrails may carry setup
not yet present mid-DAG** (the integration-gate task can legitimately depend on artifacts only built
late), so re-running them at an early union would false-red. A per-guardrail `scope:` field lets
`plan-breakdown` mark *the right* guardrails (the build, the tests) as integration-scoped wherever
they live, decoupled from which task is the terminal sink. This is a real schema addition (SSOT §4.3)
— flagged for the PO (Decision 2), recommended.

### 5. `--merge-on-success` (plan branch → user's original branch; AI-merge withheld)

Default OFF (honest-halt default): the run leaves `guardrails/<plan-name>` for the user to review and
merge. `--merge-on-success` (or `guardrails.json: mergeOnSuccess`) opts into end-of-run delivery —
**only if the whole run went green** and the terminal gate passed: `git merge --ff-only` when the
user's branch has not advanced since run start, else a real `git merge`. **AI-merge is WITHHELD
here** (Decision 3): a conflict, a failed post-merge re-verify, or a dirty user working tree halts to
**needs-human** with the plan branch intact — never a force-overwrite, never an AI auto-resolve of
the user's own commits. (This is the one place v1 runs a merge that can hit a human; bounded:
at-most-once, only on opt-in, only after a green run.)

### 6. How narrow scopes → small clean merges → cheaper unions (the cross-angle thesis)

The three mechanisms compose into **one causal chain**, which is the PO's core insight made
mechanical:

1. **Narrow `writeScope`** → the write-scope check (§2) forces each task's diff to stay inside a
   small declared surface, or fail **early, locally, as a single-task honest failure** (a retry with
   feedback, not a late tangled conflict).
2. **Small, disjoint diffs** → when two segments do fan-in (§4), git auto-merge succeeds far more
   often (fewer overlapping hunks), and when it conflicts the conflict is **small** (few files, few
   hunks) → **AI-merge is tractable** (a bounded, low-blast-radius resolution the two deterministic
   checks can actually certify).
3. **`writeScope.Overlaps` predicts unions** → the scheduler uses `Overlaps` as a **soft hint**
   (Decision 4) to prefer scheduling **disjoint** ready tasks into the `maxParallelism` slots,
   reducing the rate of concurrent-sibling races (non-FF integrations) — *not* a hard lock (a hard
   scope-lock would re-import the rejected shared-workspace design's bug-prone scope-serializer for a
   mere throughput gain; the DAG `dependsOn` edge is the real serializer). `guardrails-review` flags
   **independent siblings whose scopes Overlap** (WEAK, conflict-risk → suggest a `dependsOn` edge or
   a re-breakdown). **Because the hint has cross-task reach** (an under-detecting `Overlaps` puts a
   colliding pair in parallel — §2.1 downgrade note, §DA-5), `Overlaps` keeps the full fuzz rigor,
   including the completeness property (§2.2).
4. **Fewer, smaller unions** → fewer expensive re-verifies under the serialize lock, and the unions
   that do happen are the cheap kind. Narrow scopes are thus the upstream lever that makes the whole
   integration tail affordable. This is the mechanism the PO intuited: *"a broad surface may cause
   the human to ask plan-breakdown to go more granular"* — granularity is a throughput lever, and
   the scope check + review probes are how the system surfaces the broad-surface smell.

### 7. Resume / crash — reconciliation against the plan branch (preserved, with the FF wrinkle)

The plan branch's first-parent **merge commits** remain the durable record (plan 07's resume-by-
trailer, **preserved**). Every non-FF integration and every fan-in writes a merge commit with the
fixed-form `Guardrails-Task: <taskId>` / `Guardrails-Run: <runId>` trailers; detection walks the
plan branch's first-parent history and reads them; the merged set = trailers reachable from the plan
branch tip matching the resuming `runId`. **No second-parent-ref check** (illusory — merge commits
store SHAs, not ref names).

**Only trailers reachable from the PLAN-BRANCH TIP are authoritative (W-1).** Resume reads trailers
**exclusively** by walking back from the plan-branch tip — a `Guardrails-Task:` trailer on **any other
ref** (a surviving segment branch, a per-attempt branch, an abandoned fan-in branch) is **NOT**
authoritative and must be ignored. A crash can leave a segment ref pointing at a task commit that
**never FF'd into the plan branch**; if resume read trailers across all refs it would treat that task
succeeded and skip re-running it — a false-green. Therefore: (a) detection walks from the plan-branch
tip only; and (b) **`--fresh` and the resume prune `git branch -D guardrails/<runId>/*` BEFORE any
resume logic reads trailers**, so a stale segment ref cannot even be consulted. Naming is consistent
(N-1): segment branches are `guardrails/<runId>/<segmentId>` and the `branch -D` glob
`guardrails/<runId>/*` matches them (and any per-attempt suffix under that namespace).

**The FF wrinkle (NEW vs plan 07 — and the one genuinely new resume question).** A **fast-forward**
integration creates **no merge commit** — the plan-branch tip moves to a *plain* segment commit. So
the resume authority cannot be "first-parent *merge* commits" alone. **Resolution: the harness writes
the `Guardrails-Task:`/`Guardrails-Run:` trailer on the (possibly plain, FF'd) task commit itself**,
not only on merge commits. The integrated set = **all commits reachable from the plan-branch tip
carrying a trailer matching the resuming `runId`** (whether plain FF'd commits or merge commits). This
is a strict superset of plan 07's rule (which only ever produced merge commits) and is sound for the
same reason: the trailer is the harness-written record on a branch it solely owns and only appends to;
a task is in the integrated set **iff** a trailer-bearing commit for it is reachable from the tip.
Because the FF move and the trailer-bearing commit are the **same commit** (the commit was created in
the segment worktree at task success, *before* the FF), the crash windows are the same shape as plan
07's:
- **Crash before the FF/integration:** no trailer reachable from the plan-branch tip → task not in
  the integrated set → re-run. Its fragment (if written) re-merges idempotently in its own namespace.
- **Crash after the integration commit, before the journal:** the trailer is reachable → task treated
  succeeded (the commit beats a lost journal write); its fragment is already in `state.json` (B1
  order: fragment before commit). Consistent.

**Retry/orphan handling on resume.** Orphan segment/fan-in worktrees are pruned (`git worktree prune`
+ `remove --force` for trees not about to re-run); the integration worktree is **reattached, never
pruned**. A re-run task `git reset --hard <taskBase> + git clean -fd` in its (reused or recreated)
segment worktree — the same retry mechanism as in-run, preserving upstream commits. `--fresh` wipes
the worktree root, prunes, and deletes the abandoned plan branch + per-attempt branches.

### 8. Logs layout (PO #8 — `logs/` elevated to a sibling of `state/`, divided by `runId`)

Now that `runId` exists, the plan-07 `state/logs/<task>/attempt-N/` layout buries logs inside the
harness-state directory and does not separate runs. **Decision 5: elevate `logs/` to a sibling of
`state/` and divide by `runId`:** `logs/<runId>/<task-id>/attempt-N/`. Rationale: (i) logs are the
artifact a human navigates most (the log viewer, post-mortems) — making them a top-level sibling
makes them findable without spelunking `state/`; (ii) dividing by `runId` keeps an overnight re-run's
logs from interleaving with the prior run's, and lets `--fresh` / retention act per-run; (iii) it
cleanly separates *harness-owned mutable state* (`state/`) from *append-only audit* (`logs/`). The
per-attempt file set (§8 SSOT) is unchanged — only the parent path moves. `GUARDRAILS_LOG_DIR` now
resolves under `logs/<runId>/...`. (I considered "divide by runId but keep under `state/`" — it gets
(ii) but not (i); the PO explicitly asked for *findable*, so the elevation wins. Flagged Decision 5.)

---

## Decisions flagged for the product owner

Each with my recommendation. These are the load-bearing choices where I diverge from a literal
reading of the brief, or where a real schema cost is incurred.

**Decision 1 — Continuous FF-integration into the plan branch vs the PO's literal "merge at the
chain tail only."** **Recommend: continuous FF-integration.** The PO's reuse model implies a segment
worktree merges into the plan branch only when its chain ends. I integrate *every* task — but a
linear chain produces **fast-forward integrations** that are **free** (no union, no re-verify; the
plan-branch tip just advances to an already-verified commit), so the **worktree-reuse intent is
fully kept** (one segment worktree still carries the chain; the FF is a ref move, not a re-checkout).
The win: the plan branch is the **single durable resume truth** (plan 07's resume-by-trailer survives
almost verbatim — see the §7 FF wrinkle), and only fan-ins / concurrent-sibling races ever pay a real
re-verify. The literal "tail-only" model would defer all integration to chain ends, making resume
reconstruct partial chains from segment worktrees (fragile) and bunching all re-verify work at the
tail. **This DIVERGES from the brief's literal wording — calling it out prominently.** The reused
segment worktree remains the EXECUTION locus; the plan branch is the INTEGRATION + resume truth.

**Decision 2 — Integration-guardrail-set mechanism: per-guardrail `scope:` field (v1-B) vs reuse the
`integrationGate` task's guardrail commands at union points (v1-A).** **Recommend: the per-guardrail
`scope: "integration" | "local"` field (v1-B).** The explorer leaned v1-A (no new field) but flagged
the seam as unclean. The deciding factor: an `integrationGate` **sink task may carry setup not present
mid-DAG** (it can depend on artifacts built only late), so re-running its commands at an early union
would false-red. A per-guardrail `scope:` field lets `plan-breakdown` mark *the build and the tests*
as integration-scoped wherever they live, decoupled from the sink. **Cost: a real schema addition**
(SSOT §4.3) and a `plan-breakdown` emission change. I judge the cleanliness worth it — and it makes
the terminal gate and the per-union re-verify literally the same guardrail set, which is the coherence
the brief demands. If the PO wants to minimize v1 schema surface, v1-A is the fallback **only if**
`plan-breakdown` is disciplined to keep integration-gate guardrails setup-free.

**Decision 3 — AI-merge withheld at the `--merge-on-success` user-branch boundary.** **Recommend:
withhold (PO confirms).** AI-merge resolves *harness-internal* unions (fan-in, non-FF integration)
where both sides are bytes the harness itself produced and re-verified. The user's mid-run commits to
their own branch are **un-gated bytes the harness never integration-tested** — auto-resolving them
with an AI is exactly the silent-stale-delivery hazard. A conflict at this boundary ⇒ needs-human,
plan branch intact. The default-OFF posture means the common path never runs this merge at all.

**Decision 4 — Scheduler `Overlaps` soft-hint in v1 vs fast-follow.** **Recommend: keep it in v1, as
a cheap soft hint.** It is the most deferrable piece, but it is *cheap* once the matcher exists
(`Overlaps` is already built for `guardrails-review`'s overlap probe), and it directly serves the
PO's throughput goal (prefer disjoint ready tasks to fill `maxParallelism: 3` slots, reducing non-FF
races). It is a **hint, never a hard lock** — a hard scope-lock would re-import the rejected
shared-workspace design's bug-prone scope-serializer for a throughput-only gain. If v1 schedule pressure bites, this is the safe thing to
defer (the DAG still serializes correctly without it); but I recommend keeping it.

**Decision 5 — Logs layout: elevate `logs/` to a sibling of `state/` AND divide by `runId`.**
**Recommend: do both** — `logs/<runId>/<task-id>/attempt-N/`. Elevation answers the PO's "findable"
ask; per-`runId` division stops re-runs interleaving and lets retention act per-run. The per-attempt
file set is unchanged. (Alternative "by runId under `state/`" gets separation but not findability.)

**Decision 6 — Re-verify default at union points = full integration set (vs build-level-only opt-in).**
**Recommend: full integration set by default.** At every union point, re-run the **full**
integration-guardrail set (build + tests + any other integration-scoped guardrail). A
"build-level-only at union points, full suite at the terminal gate" mode is a **documented
non-default** throughput opt-in for plans whose test suite is too slow to re-run per union — but it
**widens the residual** (a test-only-detectable union break survives to the terminal gate), so it is
opt-in, not default. The default keeps each union honestly certified.

**Decision 7 — `git clean -fd` vs `-fdx` on retry.** **Recommend: `git clean -fd` (keep ignored
build caches).** On a `reset --hard <taskBase>` retry, `-fd` removes untracked files but **keeps
git-ignored** ones (`bin/`, `obj/`, package caches) — so a retry does not pay a full rebuild from
cold. `-fdx` (which also nukes ignored files) would be safer against a "stale build cache poisons the
retry" failure but is **expensive** on a large .NET repo (cold rebuild every retry). **Test note:** a
Stage-2 test must pin that a retry with `-fd` does not leave a stale-artifact false-green (a task
whose retry passes only because a prior attempt's `bin/` output lingered) — if that test ever fails,
the build guardrail's own clean step is the fix, not `-fdx` globally. Recommend `-fd` with that test
as the guard.

---

## Honest costs / limits of THIS approach

The feature false-greened twice. So, plainly — what this costs and where it is the wrong tool.

1. **Disk amplification (mitigated by reuse, NOT eliminated) — the honest peak-disk math the PO
   accepted (N-3).** `maxParallelism` defaults to **3** because the product owner explicitly chose it
   for demos; this is kept. The honest cost of that choice, stated so the PO sees what they accepted:
   the **peak working-tree count ≈ (antichain-width + 1 integration worktree + transient fan-in
   trees)**, so **peak disk ≈ (antichain-width + 1 + transient fan-in) × working-tree-size**. At
   `maxParallelism: 3` with a fan-in in flight that is up to ~`(3 + 1 + 1) × working-tree-size` of
   *checked-out* files at the worst instant. **Chain-reuse is the big lever** (a linear chain is ONE
   worktree, not N) and **shared `.git/objects` is free** (history/blobs are shared; the multiplier is
   on **working-tree** size, not **repo** size). **Reset-based retries** (`reset --hard <taskBase>` +
   `clean -fd`, no re-checkout) are the **real Windows disk win** — plan 07's discard-and-recreate paid
   a full checkout per retry; this design pays zero. **CoW / sparse / worktree-pooling are v2** (not
   v1, despite the PO asking) — so the peak-disk number above is the cost the PO is accepting in v1:
   - **Copy-on-write** — NTFS (the common Windows case) **has no reflink**; CoW needs ReFS-only
     P/Invoke (`FSCTL_DUPLICATE_EXTENTS_TO_FILE`), a platform-specific surface I will not put on the
     v1 critical path. APFS/btrfs reflinks exist but the cross-platform abstraction is v2 work.
   - **Sparse checkout** — the **integration/gate worktree needs the WHOLE repo** to run the
     whole-repo terminal gate; a sparse-by-scope checkout *cannot run it*. Sparse segment worktrees
     are conceivable but interact badly with fan-in (a union may touch files outside both segments'
     sparse cones). Deferred.
   - **Worktree pooling** — reusing a freed worktree for a new segment (re-`reset` instead of
     re-`add`) is a sound v2 optimization but adds lifecycle state; v1 ships chain-reuse + reset-retry
     as the pragmatic 80%.
   **Honest framing to the PO:** the brief asked for CoW/sparse/pooling **in v1**; I am delivering the
   *high-leverage* disk wins (chain-reuse + shared objects + reset-retries) in v1 and **deferring
   CoW/sparse/pooling to v2 with the technical reasons above**. This is a scope pushback, flagged.
2. **Windows MAX_PATH.** A deep worktree root + deep source can exceed 260 chars. Mitigated by a short
   temp root, a pre-flight warning (**GR2016 — a FRESH code**; the old plan-07 draft cited "GR2014",
   which is **taken by the live triad** `RestoreOnRetryWithoutCaptureHashes` and is not available),
   and documenting `core.longpaths`. Honest residual: an un-configured Windows box with a deep repo can
   still fail — an honest error, not silent corruption.
3. **Serialized integration throughput.** One global serialize-merges lock; the **B1 atomic settle +
   any non-FF re-verify** run under it. FF-integrations are cheap (a ref move), so a linear-heavy DAG
   barely contends — but a wide fan-in-heavy DAG serializes its unions, and each union re-verify is
   the **full integration-guardrail set** (Decision 6). The AI-merge + its re-verify run OFF the lock
   (in the private fan-in worktree), so the lock holds only the final integration + its staleness
   re-verify — but that is still serial. **Per-segment merge concurrency is v2** (multiple unions
   settling at once); v1 keeps one lock. The parallelism win is in *generation* (concurrent actions),
   not *integration verification*.
4. **AI-merge cost + residual (named, not oversold).** Every conflict the AI resolves is a prompt
   invocation (cost + latency, charged against `maxCostUsd`), plus the re-verify. The **named
   residual:** a cross-cutting guardrail **mis-classified as `local`** can let a broken union pass the
   per-union re-verify (which runs the union task's full set + every colliding sibling's full set + the
   integration-scoped set) — **bounded by the terminal gate. Whether this is "no wider than plan 07"
   is CONDITIONAL (W-4):** plan 07 re-verified every hop (N chances); plan 08 has one terminal gate, so
   the bound holds *only if the terminal gate exercises the cross-hop/cross-union interaction*. This
   plan makes a missing/thin terminal gate a HARD validation error (GR2017 missing / GR2018 empty-set)
   precisely to keep the conditional from silently failing. AI-merge does **not widen this** beyond a clean auto-merge
   (the re-verify is the leveler — an AI-resolved union faces exactly the same gate). The default =
   full integration set at union points; the "build-level-only at union points" opt-in (Decision 6)
   *does* widen it and is therefore non-default.
5. **Scope-as-theater.** A `writeScope` declared too loose (e.g. a bare top-level dir) makes the check
   pass vacuously — protection in name only. `plan-breakdown` self-reports broad scopes as a
   granularity smell and OMITS the field rather than emitting a vacuous `**`; `guardrails-review`
   flags vacuous/over-broad scope (BLOCKER/WEAK) and scope-intent mismatch (WEAK). These are
   imperfect mitigations of a real residual — a determined-loose scope is theater the review may miss.
6. **Where this is simply WRONG.** Non-git workspace → validation error (no silent fallback). A
   workspace that tracks build output → the merge surface churns artifacts (mitigated by `state/`
   outside the merge + the W3 porcelain check). A flailing agent escaping its worktree by absolute
   path → physical isolation protects the *tree*, not the *filesystem* (the same residual every
   sandbox has; `allowedTools` is the real boundary, orthogonal).
7. **Git + AI as preconditions.** "Light setup" now includes git, *and* AI-merge adds prompt cost to
   the integration path — reasonable for the audience, but not nothing.
8. **Net residual carried into the handoff (promoted to decision altitude — N-2).** The PO and the
   implementing agents should see these in the body, not buried in the self-critique:
   - **(a)** AI-merge re-runs the colliding siblings' full guardrails (closing the dropped-hunk hole,
     B-3), but does **NOT** close a *semantic* conflict in cleanly-merged files — the **terminal gate
     is load-bearing** there, and its sufficiency is **conditional** (W-4), which is why a missing/thin
     gate is now a hard validation error.
   - **(b)** FF makes linear chains free at the cost of **no per-hop re-verify** — a named regression
     vs plan 07, bounded by the terminal gate, **no wider only IF** that gate exercises the cross-hop
     interaction (W-4 conditional).
   - **(c)** Write-scope can be **theater** if declared too loose; review probes mitigate, imperfectly.
   - **(d)** FF resume requires the **trailer-on-every-integrated-commit** rule AND
     **plan-branch-tip-only** trailer authority (stale non-plan-branch refs ignored; `branch -D` before
     resume reads trailers — W-1).
   - **(e)** Reset-retry is safe **iff** `taskBase` is captured-at-assignment (≠ `preHead`) AND no
     sibling commits onto a live reused segment branch (two tested scheduler invariants).
   - **(f)** The CHECK's matcher bug is downgraded to own-false-red/missed-catch, but
     **`Overlaps`-the-scheduling-hint retains cross-task reach** (composes with AI-merge) and keeps full
     fuzz rigor including the completeness property (W-5/W-6).
   - **(g)** The matcher's permissive-bug fix must be proven by a tests-FAIL-on-the-naive-matcher fuzz
     gate first, or "fixed" is unverified.

---

## Devil's-advocate self-critique

The prior two designs false-greened. I am my own harshest critic on the five hazards the brief names.

**1. AI-merge residuals — "the byte-producer + blast-radius + re-verify still let a broken union
pass."** *Conceded as the sharpest hazard, with the B-3 fix now applied.* The dropped-hunk hole is
**now closed for the colliding sibling** because step 3 re-runs **every colliding sibling's FULL
guardrail set unconditionally** — no touched-files skip, because the AI may have deleted the sibling's
source while leaving the sibling's test file untouched (which a touched-files filter would skip exactly
when needed). So the cheapest *remaining* wrong merge is not the dropped hunk but a **semantic conflict
in a cleanly-merged file** the AI never touched (a sibling renamed a symbol the merging task calls, no
textual conflict), exercised only by a guardrail mis-classified as `local`. That ships broken until the
terminal gate — **IF** the terminal gate's whole-repo suite exercises it; if the behavior is a narrow,
locally-tested invariant *and* the terminal gate is weak, it can false-green. The terminal gate's
sufficiency is **conditional** (W-4), which is why a missing/thin gate is now a **hard validation
error** (GR2017 missing gate / GR2018 empty set), not advisory. **My honest claim is NOT "AI-merge is safe" — it is "AI-merge is
no less safe than a clean git auto-merge of the same hunks."** Both face the identical re-verify; the
AI doesn't get a weaker gate. The blast-radius check closes the *cross-file* clobber (the rejected
shared-workspace design's exact sin) mechanically; the B-3 colliding-sibling re-verify closes the
*in-conflicted-file dropped hunk*; the residual is the *semantic drift in untouched files*, caught only
by an integration-scoped guardrail + the terminal gate. **Feasibility-reality note:** the AI worker is
**not** "the existing seam returning bytes" — `PromptResult` returns metadata only, so AI-merge needs a
new `GUARDRAILS_MERGE_*` env contract on the existing on-disk file convention + a distinct merge prompt
profile (§4 step 2); none of that weakens the gate, but the design must not pretend the seam is free.
`plan-breakdown` must scope the build + whole-suite tests as `integration`, and `guardrails-review`
must flag a thin terminal gate (BLOCKER). I do not claim closure. **The 1-retry bound also means a
genuinely hard conflict escalates to a human fast** — the correct failure, not a false-green.

**2. Scope-as-theater — "a declared writeScope catches nothing yet looks protective."** *Conceded —
this is the honest weakness of write-scope-as-check.* The cheapest theater: a task declares
`writeScope: ["src/"]` (a bare top-level dir) and edits anything under it — the check passes
vacuously, and a reviewer skimming the task sees a scope and assumes discipline. What a too-loose scope
fails to catch: an out-of-intent edit *within* the loose cone (the whole point — narrow scope — is
gone). **The TDD test-exclusion has its own hole:** excluding the test files from the implementation's
scope stops the implementation from *editing* the tests, but it does **not** stop the implementation
from making the tests pass *vacuously* by other means (e.g. editing a shared helper the tests import,
if that helper is in scope) — the test-author task's guardrails (tests-fail-on-stub) are the real
protection there, not the scope check. **Honest position:** write-scope-as-check is **mostly
merge-hygiene + planning discipline + test-protection, only partly correctness** — I state this in §2
and do not let it masquerade as a correctness guarantee. The review probes (vacuous/over-broad =
BLOCKER/WEAK) are the imperfect mitigation; a determined-loose scope is theater they may miss. Its
*distinct, real* value is the early-local-failure → small-fan-in → tractable-AI-merge chain (§6),
which is hygiene, not correctness — and I sell it as exactly that.

**3. The FF-integration resume story — "worktree reuse + commit-on-top + FF breaks resume-by-trailer."**
*This is the genuinely new risk vs plan 07, and I take it seriously.* Plan 07's resume read
*first-parent merge commits*' trailers; **a FF integration produces NO merge commit** (a plain commit,
plan-branch tip advanced). If resume only read merge commits, every FF'd linear task would be invisible
to resume → re-run → **double-work** (or, if the re-run's commit doesn't match, a divergent tip). The
fix (§7): **the harness writes the trailer on the task commit itself**, and resume reads the integrated
set as **all trailer-bearing commits reachable from the plan-branch tip** (plain or merge) — and
**ONLY from the plan-branch tip** (W-1): a `Guardrails-Task:` trailer on a surviving segment or
per-attempt ref that never FF'd is **not** authoritative and is ignored, and `--fresh`/prune does
`git branch -D guardrails/<runId>/*` **before** resume reads any trailer, so a stale ref cannot be
consulted (the Stage-2 crash-after-segment-commit-before-FF-with-a-surviving-stale-ref test pins this).
The crash window the FF/reuse model has that plan 07's discard-and-recreate did not: **a crash mid-segment with
several FF'd commits already on the plan branch but the segment worktree holding an uncommitted
in-progress task** — plan 07 would discard the whole per-task worktree and re-run cleanly; here the
reused segment worktree has real upstream commits in it. Resolution: those upstream commits are
**already trailer-bearing on the plan branch** (they FF'd), so resume treats them succeeded and re-runs
**only** the uncommitted task via `reset --hard <taskBase> + clean -fd` in a freshly-recreated segment
worktree forked off the current plan-branch tip (which contains the FF'd upstreams). **The reuse is a
runtime optimization; the plan branch is the truth** — so a crash that loses a segment worktree loses
no integrated work. I am confident in this *given* the trailer-on-every-integrated-commit rule; without
it, the FF model is unsound for resume, and that rule is therefore a Stage-2 test gate
(resume-after-FF-before-journal).

**4. Reuse-retry-loses-upstream-work — "`reset --hard <taskBase>` can nuke a sibling's merged work in
a reused worktree."** *The scariest-sounding hazard; here is why it does not bite, and the one case
where it would.* In a **reused segment (linear chain)** worktree, `taskBase` = this task's start
commit = the upstream task's committed tip. `reset --hard <taskBase>` discards **only this task's**
uncommitted changes and keeps every upstream commit (they are ancestors of `taskBase`). So a linear
retry is safe. In a **fan-in** worktree, `taskBase` = the **post-fan-in union commit** (after the
others were merged in) — `reset --hard <taskBase>` keeps the union and discards only the fan-in task's
own action changes. The case where it **would** bite: **if `taskBase` were mis-computed** as the
*plan-branch* `preHead` (the integration rollback point) instead of the *segment/union* commit — that
would reset across upstream commits and lose work. **`taskBase` and `preHead` are deliberately
distinct** (§1, §3) precisely to prevent this; conflating them is the bug, and it is a Stage-2 test
gate (retry-preserves-upstream-commits asserts the segment worktree still contains the upstream's file
after a reset-retry). The other case: a reset-retry must **never** run in a worktree another live task
is using — guaranteed because a segment worktree is single-task-at-a-time (the chain is sequential)
and a fan-in private worktree is owned by exactly one fan-in task. So the hazard reduces to "compute
`taskBase` correctly + never share a tree," both of which are testable invariants, not hopes.

**5. The matcher — "the permissive-bug downgrade from cross-task corruption to own-false-red is
not actually true, because the scheduler uses `Overlaps` as a hint."** *The subtlest hazard, and I
concede the scheduler interaction needs care.* The downgrade (stated at the point of claim in §2.1, not
buried here — W-5) rests on the check **writing nothing** — a mis-scoped `IsInScope` can only false-red
(block a legitimate task) or miss-catch (pass an out-of-scope edit), both confined to **one task's own
verdict**; neither writes another task's files (the rejected shared-workspace corruption vector is
*structurally* gone because there is no enforcer). **Where the adversary has a point:** the scheduler's
`Overlaps` hint. If `Overlaps` is **buggy and returns false** for two genuinely-overlapping scopes, the
scheduler may run them **concurrently**, raising the chance of a real fan-in/non-FF conflict later — but
that conflict is then **caught by git + AI-merge + re-verify**, not by the scope check, so the
**immediate** clobber is caught and the worst case is *more merge work*, **not cross-task corruption**
(the merge path is the safety net, the hint is only a throughput optimization). If `Overlaps` returns
**true** for disjoint scopes, the scheduler merely **serializes** tasks that could have run in parallel
— a throughput loss, not a correctness bug. **So the read-only downgrade holds for the CHECK** — a
matcher bug costs a false-red, a missed-catch, or a scheduling inefficiency, never the shared-workspace
design's irreversible cross-task corruption. **WHERE THE ADVERSARY GENUINELY WINS — the composition
with AI-merge.** A buggy `Overlaps` that *under-detects* puts two actually-overlapping tasks on a
collision course; if their later fan-in conflict is resolved by the AI dropping a hunk and the dropped
content isn't integration-scoped, the broken union can pass to the terminal gate. So
`Overlaps`-as-scheduling-hint **retains cross-task reach** — it is not "downgraded to own-task" the way
`IsInScope`-the-CHECK is. This is why `Overlaps` keeps the **full fuzz rigor** — both the
membership-implies-overlap law AND the **completeness property (no false negatives, W-6)**, which is the
one that directly constrains this under-detection hazard; membership-implies-overlap alone does not. The
union re-verify runs the **colliding siblings' full guardrails unconditionally** (§4 step 3, B-3,
closing the dropped-hunk half), and the terminal gate is **load-bearing**. The honest statement: the
CHECK's read-only verdict path has no cross-task reach; `Overlaps`-the-hint does, and is treated with
the same rigor, not as a "mere throughput knob." The **27-row truth table + the two fuzz properties**
are a milestone gate, and the fuzz suite **must be proven to FAIL against a deliberately-naive
permissive matcher first** — a fuzz suite that passes against the naive matcher proves nothing, which
is exactly how a permissive segment-matcher ships undetected.

**Net:** the design survives self-critique with the claim scoped honestly — **worktrees isolate
(real, free); the write-scope check is hygiene + test-protection, only partly correctness; FF makes
linear chains free; fan-in/sibling unions get git→AI→re-verify with the deterministic re-verify as
the verdict; the terminal gate backstops; the atomic settle + `reset --hard` rollback +
resume-by-trailer carry over.** The residuals carried into the handoff (also promoted to §Honest-costs
item 8 at decision altitude): (1) AI-merge re-runs the colliding siblings' **full guardrails
unconditionally** (B-3 — closing the dropped-hunk hole) but does NOT close the
semantic-conflict-in-cleanly-merged-files residual — the **terminal gate is load-bearing** there, and
its sufficiency is **conditional** (W-4), so a missing/thin gate is a hard validation error; (2) FF
makes linear chains free at the cost of **no per-hop re-verify** (a named regression vs plan 07,
bounded by the terminal gate, **no wider only IF** the gate exercises the cross-hop interaction —
conditional, W-4); (3) scope can be theater (review probes mitigate, imperfectly); (4) FF resume
requires the **trailer-on-every-integrated-commit** rule AND **plan-branch-tip-only trailer authority**
(stale non-plan-branch refs ignored; `branch -D` before resume reads trailers — W-1; tested — §7); (5)
reset-retry is safe *iff* `taskBase` is captured-at-assignment (≠ `preHead`) AND no sibling commits
onto a live reused segment branch (two tested scheduler invariants — §1); (6) the CHECK's bug is
downgraded to own-false-red/missed-catch, but `Overlaps`-the-scheduling-hint retains cross-task reach
(composes with AI-merge) and keeps full fuzz rigor including the completeness property (W-5/W-6); (7)
the matcher's permissive-bug fix must be proven by a tests-FAIL-on-the-naive-matcher fuzz gate. **And
the feasibility realities the design now states plainly:** the AI-merge worker needs a new merge env
contract + a distinct prompt profile (not the existing seam returning bytes); the mid-DAG re-verify is
a NEW attempt-decoupled public seam (not the `internal sealed`, attempt-bound `GuardrailRunner`); and
the global serialize-merges lock is NET-NEW (no existing merge lock to wire against — and `WorkspaceLock`
is being deleted, not reused).

---

## Process / staging (test-first; dogfood is a capstone, NOT the gate)

The plan-07 / prior-remediation discipline, carried forward: **a parallelism mechanism proven only by
a wall-clock green proves nothing.** TEST-FIRST against the real executor seam, verify independently,
dogfood as a capstone.

- **Stage 1 — Contract first (this doc → SSOT).** Apply the §Schema-changes to the SSOT in the same
  change the harness work begins. Owner: `guardrails-architect` proposes; lead applies.
- **Stage 2 — Hand-implement, agent-team, test-first.** `guardrails-test-author` writes the
  real-executor suite *first* (each test proven to FAIL against the current harness), then
  `guardrails-harness-developer` implements to green. The load-bearing tests:
  - **FF-integration-is-free + trailer-on-FF-commit:** a linear chain settles via `merge --ff-only`,
    produces NO merge commit, **no re-verify runs at integration**, and each FF'd commit carries the
    `Guardrails-Task:`/`Guardrails-Run:` trailer.
  - **non-FF-union-re-verifies (the false-green gate, B1):** two siblings settle into the plan branch
    such that the second's integration is non-FF and the merged bytes fail to build; assert the
    re-verify FAILS, `reset --hard preHead` (plan-branch HEAD == preHead, no merge commit), task
    needs-human, **and the B1 four-effect rollback** (journaled needs-human not Succeeded; no fragment
    in `state.json`; `mergeSequence` not consumed; user branch untouched). The direct analogue of the
    prior remediation's concurrent-sibling-corruption test.
  - **triad-teardown (Stage-1 prerequisite):** after teardown, a plan that still declares
    `exclusive`/`captureHashes`/`restoreOnRetry` either ignores them or fails loudly (decide and pin);
    `WorkspaceLock`/`CapturedFileStore`/`FileHashCapture`/`RestoreAncestorCaptures` are gone; the two
    triad validators are gone; **GR2013/GR2014 no longer mean the triad checks**; ~147 triad test refs
    + the `StatePlanBuilder` fakes re-baseline green.
  - **re-verify-seam (the NEW attempt-decoupled seam, feasibility fix 2):** `IReVerifier` runs a given
    guardrail set against arbitrary worktree bytes with **no** attempt `logDir`, no attempt number, no
    action result; assert it does NOT depend on `GUARDRAILS_ACTION_*`; both the terminal gate and a
    union re-verify call it.
  - **merge-lock-is-net-new (feasibility fix 3):** the integration `SemaphoreSlim(1,1)` serializes two
    concurrent settles into the plan branch; assert it is a distinct lock from `StateManager`/`RunJournal`
    `_gate` and that `WorkspaceLock` is gone.
  - **ai-merge-byte-producer + merge-env-contract + blast-radius (feasibility fix 1):** the worker
    receives `GUARDRAILS_MERGE_BASE/_OURS/_THEIRS` on disk, writes the resolution to
    `GUARDRAILS_MERGE_OUT`, and the harness reads `_OUT` (NOT `PromptResult` bytes — there are none);
    (i) a conflict the AI resolves cleanly (no markers, in-bounds) → re-verify is the verdict, union
    settles; (ii) an AI resolution that writes a file **outside** the conflicted set → **detected,
    discarded (`reset --hard`), needs-human** (the cross-file clobber, mechanically closed); (iii) an
    AI resolution that leaves a marker → detected, needs-human; (iv) budget exhausted after 1 retry →
    needs-human. **`PromptResult.IsError`/the exit code is never read as a verdict.**
  - **ai-deleted-hunk → colliding-sibling re-verify catches it (B-3, the load-bearing gate):** the AI
    resolves a conflict by **dropping a colliding sibling's source hunk** (no markers, in-bounds, bytes
    compile, the merging task's own guardrails pass). Assert the union is caught because the **colliding
    sibling's FULL guardrail set re-runs unconditionally** — including a sibling `local` guardrail whose
    file the merge did **not** touch; assert that with a touched-files local-skip wrongly applied to the
    sibling, this test FAILS (the hole the local-skip would re-open). This test is the proof B-3 is
    closed.
  - **ai-merge-off-lock / integration-under-lock:** the AI-merge + its re-verify run in the fan-in
    private worktree OFF the global lock (a concurrent sibling integration proceeds meanwhile); only
    the final integration of the verified result is under the lock, with a staleness re-verify against
    the current plan-branch bytes.
  - **integration-guardrail-set scope (local-skip is DISTANT-NON-COLLIDING-ONLY):** an
    `integration`-scoped guardrail re-runs at every union point; a `local`-scoped guardrail of a
    **distant, non-colliding** task re-runs at a union **only if** the merge touched its files; a
    **colliding sibling's** `local` guardrail re-runs **regardless** of touched-files (the B-3 split);
    the terminal `integrationGate` sink runs the same integration set on the final HEAD.
  - **writescope-check (deterministic, read-only):** an out-of-scope edit → guardrail-failure → retry
    with feedback naming the path → needs-human; **the workspace is never written by the check** (a
    rename presents as paired D+A, both must be in scope; a deletion's path must be in scope); a task
    with **no** `writeScope` runs with no check.
  - **writescope-tdd-exclusion:** an implementation task whose scope excludes the test files, editing
    a test file → check fails (the triad replacement).
  - **matcher proof harness (tests-FAIL-on-the-naive-matcher FIRST):** the **full 27-row truth table**
    (§2.1) for `IsInScope` + **two** fuzz properties — membership-implies-overlap AND `Overlaps`
    completeness (no false negatives, §2.2). **The whole suite must be proven to FAIL against a
    deliberately-naive permissive matcher (the "skip the `*`-segment's literal prefix/suffix" shortcut)
    before the real matcher is written** — rows 1/4/7/18/19/21/23/25 are the permissive traps that must
    go red against the naive matcher.
  - **retry-preserves-upstream-commits (hazard 4 gate):** a reset-retry in a reused segment worktree
    leaves the upstream's committed file present (asserts `taskBase ≠ preHead`); `-fd` keeps ignored
    build caches, no stale-artifact false-green (Decision 7).
  - **fork-the-rest-off-recorded-sha (W-2 gate):** a fan-out fork-the-rest dequeued **after** the
    inherit-one successor has committed onto the shared segment branch lands on the **producer's
    recorded tip**, NOT the inheritor's advanced tip.
  - **resume-after-FF-before-journal (hazard 3 gate):** kill after an FF'd task commit lands but
    before the journal write; resume reads the task succeeded **purely from the plain commit's
    trailer**, does not re-run, does not double-integrate. Companion:
    resume-after-fragment-before-commit (B1 reverse window).
  - **resume-ignores-stale-segment-ref (W-1 gate):** crash **after a segment commit but before its
    FF** leaves a `Guardrails-Task:` trailer on a surviving segment ref that never reached the plan
    branch; assert resume **re-runs** that task (the stale ref is NOT authoritative), and that
    `--fresh`/prune `git branch -D guardrails/<runId>/*` runs **before** any trailer read.
  - **merge-on-success withholds AI-merge:** a green run with `--merge-on-success` and a user branch
    advanced mid-run with a conflicting commit → the harness does NOT AI-resolve; it halts needs-human
    with the plan branch intact.
  - **terminal-integration-gate is a HARD error (W-4):** a multi-leaf or fan-in-bearing plan with no
    `integrationGate` sink fails `validate` (**GR2017, fresh**); an `integrationGate` sink with no
    `scope: "integration"` guardrail fails `validate` (**GR2018, fresh** — empty integration set); a
    green-per-task-but-broken-union run is reported not-green by the gate; a single-task/pure-linear
    plan is exempt. (Not-a-git-repo is the separate GR2015 gate.)
- **Stage 3 — Independent verification.** Full suite green on 3-OS CI; `guardrails-devils-advocate`
  runs a "cheapest wrong integration that passes these tests" pass — hunting a skippable re-verify, a
  reset that targets `preHead` instead of `taskBase`, an AI-merge whose blast-radius check is bypassable,
  a FF that doesn't write the trailer, an `Overlaps` bug that lets the scheduler corrupt (it can't —
  prove the merge path catches it), and a resume that mis-reads the FF'd-commit trailers.
- **Stage 4 — Re-dogfood as CAPSTONE.** Regenerate a real plan (this feature, or a fresh slice) under
  the verified harness; run from the global tool / `dotnet run` Debug (never the Release self-lock).
  Done when the run completes green, a human confirms the journal shows ≥2 actions overlapping with
  integrations serialized after, **a fan-in went through git→(maybe AI)→re-verify**, and the run left a
  `guardrails/<plan-name>` branch with the user's original branch untouched (dogfood runs
  `--merge-on-success` OFF). **The dogfood demonstrates; it does not prove.**

Substantial multi-finding work → the **agent team** (harness-dev + test-author + skill-author), with
hands-on lead review before each stage completes.

---

## Milestone decomposition (walking-skeleton first; re-decomposed from plan 07)

Each milestone is independently shippable + testable, in DAG order; test-author tasks precede
implementation. Plan 07's M1/M2 (de-serialize + real worktrees) are **partly reused**, but **re-sized
against the live code** (the triad is fully present on `master`); M3 is **re-decomposed** for
FF/reuse/union, and new milestones add the scope-check and AI-merge.

**Triad teardown is a prerequisite THIS plan delivers (B-2), spread across M1/M2 with the SSOT
triad-removal as a Stage-1 prerequisite.** The live triad (`TaskNode.Exclusive/CaptureHashes/
RestoreOnRetry`, `WorkspaceLock`, `CapturedFileStore`, `FileHashCapture`, `RestoreAncestorCaptures`,
the two validators `ValidateCaptureHashes`/`ValidateRestoreOnRetry`, the `exclusive` admission gate)
is **fully present** — ~147 test refs + ~118 source refs — and its diagnostic codes **GR2013/GR2014
are TAKEN** (`CaptureHashEscapesWorkspace`/`RestoreOnRetryWithoutCaptureHashes`). Every new gate in
this plan gets a **FRESH GR2015+** number, and GR2013/GR2014's triad meanings are **RETIRED in the
same change that deletes the triad** — no "reuse the freed number" (they are not freed until this plan
frees them). The teardown line items appear explicitly in M1 (channel/handle + capture seam) and M2
(`WorkspaceLock`/store/validators), and the `StatePlanBuilder` fakes + ~147 test refs are re-baselined.

- **M1 — De-serialize the scheduler against a FAKE worktree provider + the executor↔scheduler handle
  seam + BEGIN triad teardown (walking skeleton).** **Re-sized M→L (was M)** because the executor that
  gets the handle ALSO owns the live triad capture/restore seam, and the channel carries a **bare
  `TaskNode`** today (`Scheduler.cs:65` `Channel.CreateUnbounded<TaskNode>()`) with **no per-task
  envelope** to carry a handle. Deliver: `IWorktreeProvider` + `WorktreeHandle`/`IntegrationHandle` +
  `FakeWorktreeProvider`; a **per-task channel envelope** (`TaskNode` + its assigned `WorktreeHandle`)
  replacing the bare-`TaskNode` channel; the `ExecuteAsync(task, WorktreeHandle, ct)` signature change
  through `ITaskExecutor` + **every** test fake (the bare `ExecuteAsync(TaskNode, ct)` ripple); BEGIN
  triad teardown — remove the capture/restore seam from the executor (`RestoreAncestorCaptures` at
  `TaskExecutor.cs:165/287`), and drop the `exclusive` admission gate from the scheduler. Fake
  integrate is a no-op. **Exit:** 3 independent tasks run with overlapping windows (gated, not
  wall-clock), no git; the channel carries handles; the capture seam is gone from the executor. Size: L.
  Agent: `guardrails-harness-developer` + `guardrails-test-author`.
  filesTouched: `Execution/IWorktreeProvider.cs`, `Execution/WorktreeHandle.cs`/`IntegrationHandle.cs`,
  `Execution/FakeWorktreeProvider.cs`, `Execution/ITaskExecutor.cs`, `Execution/TaskExecutor.cs`
  (per-task envelope, drop `RestoreAncestorCaptures`), `Execution/Scheduler.cs` (channel envelope, drop
  `exclusive` gate); `tests/**` (fake-executor signature rewrites across all fakes + overlap gate +
  triad-ref re-baseline begins).

- **M2 — Real git worktree lifecycle incl. the plan branch + the REUSE/CHAINING topology + triad
  teardown part 2.** `GitWorktreeProvider` (plan branch `guardrails/<plan-name>` + integration worktree
  off the user's HEAD; **segment worktrees with reuse** — root fork, linear reuse, fan-out inherit-one +
  fork-the-rest off the **recorded sha** (W-2), fan-in fork; discard/prune); `TaskExecutor` cwd →
  segment worktree; **git-required validation (FRESH GR2015)**; `TaskNode.IntegrationGate` +
  terminal-gate validation (**FRESH GR2017 for missing gate; FRESH GR2018 for empty integration set**,
  W-4); MAX_PATH warning (**FRESH GR2016**); `worktreeRoot` config; **`maxParallelism` default 3** (PO
  choice; honest peak-disk math in §Honest costs); **logs layout elevated to `logs/<runId>/...`**
  (Decision 5). **Triad teardown part 2 (and the GR retirement):** delete `WorkspaceLock` + the
  `exclusive` admission gate, `TaskNode.Exclusive/CaptureHashes/RestoreOnRetry`, the two validators
  `ValidateCaptureHashes`/`ValidateRestoreOnRetry`; **RETIRE the GR2013/GR2014 triad meanings** in the
  same change (the codes are now free; reassign their constant names or remove them). **No integration
  yet** — tasks run in reused segment worktrees and are discarded. **Exit:** a linear chain reuses ONE
  worktree; a fan-out forks lazily off the recorded sha; the user's branch is untouched; non-git fails
  GR2015; a multi-leaf/fan-in plan without a gate sink fails GR2017; an empty-integration-set gate fails
  GR2018; logs land under `logs/<runId>/`; the triad is gone and GR2013/GR2014 no longer mean the triad
  checks. Size: L.
  Agent: `guardrails-harness-developer` + `guardrails-test-author`.
  filesTouched: `Execution/GitWorktreeProvider.cs`, `Execution/WorktreeManager.cs` (reuse topology,
  recorded-sha fork), `Execution/TaskExecutor.cs`, `Execution/Scheduler.cs` (plan branch at run start;
  `WorkspaceLock`/`exclusive`-gate removal; segment assignment), `Execution/WorkspaceLock.cs`
  (**deleted**), `Loading/PlanValidator.cs` (delete `ValidateCaptureHashes`/`ValidateRestoreOnRetry`;
  add gate + git-required + MAX_PATH validation), `Loading/DiagnosticCodes.cs` (**retire GR2013/GR2014
  triad meanings; add FRESH GR2015 not-git / GR2016 MAX_PATH / GR2017 missing-gate / GR2018 empty-set**),
  `Model/TaskNode.cs` (remove triad; add
  `IntegrationGate`), `Model/RunConfig.cs` (`worktreeRoot`, `maxParallelism` default 3), `State/` log
  path resolution; SSOT §1/§2/§3/§8; `tests/**` (triad-ref re-baseline completes; `StatePlanBuilder`
  fakes).

- **M3 — Write-scope CHECK + the matcher (built fresh to §2.1) + its proof harness (the determinism
  milestone).** Build `Execution/WriteScope.cs` (`IsInScope`/`Overlaps`/segment-matcher) **fresh to the
  §2.1 spec** (the prior shared-workspace matcher carried a permissive prefix/suffix-discard bug and is
  not in this tree — it may be lifted as a starting point only, then verified against the §2.1 table +
  fuzz, never trusted as-is); the `WriteScopeCheck` built-in (post-action, pre-guardrails, `git diff
  --name-status <taskBase>..<segmentHEAD>` membership; rename = paired D+A; deletion path in scope;
  absent ⇒ no check); `TaskNode.WriteScope` + validation (**escape = FRESH GR2019 error; vacuous/
  over-broad = FRESH GR2020 warning** — distinct from GR2015-2018 assigned in M2); the **27-row truth
  table (§2.1) + the TWO fuzz properties (§2.2)** as a
  milestone gate, **proven to FAIL against a naive permissive matcher first**. **Exit:** an out-of-scope
  edit fails the check → feedback → retry; a TDD implementation editing a test file fails; the matcher
  passes its full table + both fuzz properties, and the suite is red against the naive matcher. Depends
  on M2. Size: M.
  Agent: `guardrails-harness-developer` + `guardrails-test-author` (tests + table FIRST).
  filesTouched: `Execution/WriteScope.cs` (new), `Execution/WriteScopeCheck.cs` (new),
  `Execution/TaskExecutor.cs` (invoke the check), `Model/TaskNode.cs` (`WriteScope`),
  `Loading/PlanValidator.cs` (scope validation, FRESH GR codes), `Loading/DiagnosticCodes.cs`, SSOT
  §3.4; `tests/**` (truth table + both fuzz properties + check tests).

- **M4 — Integrate: the NET-NEW serialize-merges lock + the NEW attempt-decoupled re-verify seam + FF
  + non-FF union + the atomic settle (B1) + resume-by-trailer incl. the FF wrinkle + `--merge-on-success`
  (the soundness core — and where false-green risk concentrates).** **OVER-PACKED, flagged:** this
  milestone grafts a **net-new `SemaphoreSlim(1,1)` integration lock** (there is none today — fix 3),
  a **NEW public attempt-decoupled `IReVerifier` seam** (the `internal sealed` attempt-bound
  `GuardrailRunner` cannot run on arbitrary union bytes — fix 2), git-trailer resume reconciliation
  **grafted onto today's journal-only resume**, `reset --hard` rollback, the FF rule, and the
  trailer-on-every-integrated-commit + plan-branch-tip-only authority (W-1). **This is the highest
  false-green concentration in the plan; its Stage-2 tests are the MOST demanding** — the
  non-FF-union-re-verify (B1 four-effect), resume-ignores-stale-segment-ref (W-1), and
  retry-preserves-upstream-commits (taskBase≠preHead) gates are non-negotiable, each proven red first.
  **If schedule allows, split M4 into M4a (lock + `IReVerifier` + FF/non-FF settle + B1) and M4b
  (resume reconciliation + W-1 + `--merge-on-success`)** — recommended, because the resume
  reconciliation is independently testable and concentrating it with the settle is the risk.
  Deliverables: build the integration lock; build `IReVerifier`; the `MergeAndSettleAsync` refactor
  (relocate fragment-merge + `mergeSequence` + `Succeeded` into the locked settle, fixed order);
  FF-integration (free, trailer on the plain commit); non-FF union re-verify (via `IReVerifier`:
  integration set + the task's own guardrails on merged bytes); `reset --hard preHead` rollback; resume
  by trailer-on-every-integrated-commit + plan-branch-tip-only (W-1, `branch -D` before trailer read);
  `reset --hard <taskBase> + clean -fd` retry; `--merge-on-success` (AI-merge withheld here);
  terminal-gate re-verify on the final HEAD via `IReVerifier`; `RunReset` takes `IWorktreeProvider`.
  (Triad store deletes `CapturedFileStore`/`FileHashCapture` are done in M2; M4 only ensures nothing
  re-references them.) **Exit:** the FF-is-free, non-FF-union-re-verifies (B1 four-effect),
  retry-preserves-upstream-commits, resume-after-FF-before-journal, resume-ignores-stale-segment-ref
  (W-1), and merge-on-success tests pass; `IReVerifier` runs with no attempt context. Depends on M3.
  Size: L (XL if not split).
  Agent: `guardrails-harness-developer` + `guardrails-test-author` (tests FIRST).
  filesTouched: `Execution/IReVerifier.cs` (new, public attempt-decoupled), `Execution/Integrator.cs`
  (or fold into provider), `Execution/TaskExecutor.cs` (`MergeAndSettleAsync`, B1 relocation,
  reset-retry), `Execution/AttemptJournaler.cs` (settle tail under lock), `Execution/Scheduler.cs`
  (**net-new `SemaphoreSlim(1,1)`**, resume pre-pass with FF-trailer + W-1 plan-branch-tip rule,
  terminal-gate re-verify, end-of-run merge), `Model/RunConfig.cs` (`mergeOnSuccess`), `Guardrails.Cli`
  (`--merge-on-success`), `State/RunReset.cs`, SSOT §4.3/§5.3; `tests/**`.

- **M5 — AI-merge worker (NEW merge env contract + distinct profile) + the integration-guardrail-set
  scope field, CONSUMING the M4 re-verify seam (the union-resolution milestone).** **Under-sized in the
  prior draft — corrected:** the AI-merge worker is **NOT "the existing seam returning bytes"** (fix 1).
  Deliver `Execution/AiMergeResolver` over the existing `IPromptRunner` **plus a NEW merge env contract**
  (`GUARDRAILS_MERGE_BASE/_OURS/_THEIRS/_OUT` on disk — the worker writes `_OUT`, the harness reads it,
  reusing the on-disk file convention; `PromptResult` carries no bytes) **and a distinctly named merge
  prompt profile** (e.g. `ai-merge` under `promptRunners`, NOT `guardrailOverrides`-shaped — N-4); the
  two deterministic checks (no markers via `git diff --check`; blast-radius via `git status
  --porcelain`); 1-retry budget; escalation to needs-human; AI-merge wired into the §4 union path
  (fan-in + non-FF), OFF the global lock in the private worktree, **with the verdict = the M4
  `IReVerifier` re-verify** (the colliding siblings' FULL set unconditionally — B-3); the per-guardrail
  `scope: "integration" | "local"` field (Decision 2) as a **free filter** on the `IReVerifier`
  guardrail set (the seam itself is M4's deliverable, not M5's). **Exit:** the
  ai-merge-byte-producer + merge-env-contract + blast-radius + ai-deleted-hunk→colliding-sibling-catch
  (B-3) + off-lock + integration-set-scope + withheld-at-user-branch tests pass. Depends on M4. Size: L.
  Agent: `guardrails-harness-developer` + `guardrails-test-author` (tests FIRST, incl. a fake AI runner
  that writes canned merged bytes to `_OUT` + a malicious out-of-bounds writer + a hunk-dropper).
  filesTouched: `Execution/AiMergeResolver.cs` (new), `Execution/Integrator.cs` (wire AI-merge into the
  union path), `Execution/IReVerifier.cs` (scope filter — the seam is from M4), `Model/GuardrailNode.cs`
  (`Scope`), `Model/PromptRunnerConfig.cs` (merge profile), `Loading/PromptFileParser.cs`/sidecar parse
  (`scope` frontmatter/sidecar key), SSOT §4.3/§9.1; `tests/**`.

- **M6 — Skills switch-over.** `plan-breakdown` emits `writeScope` per task (TDD test-exclusion;
  OMIT for un-scopable tasks; no vacuous `**`), `scope: "integration"` on the build/test guardrails,
  the terminal `integrationGate` sink, and stops emitting the triad/`exclusive`. `guardrails-review`
  probes: vacuous/over-broad scope (BLOCKER/WEAK), scope-intent mismatch (WEAK), independent-sibling
  scope OVERLAP (WEAK), **implementation-scope-includes-its-test-files (BLOCKER)**, missing terminal
  gate (BLOCKER), thin gate (BLOCKER). `guardrails-domain-knowledge` updated (worktree reuse, scope
  check, AI-merge, integration set). **Exit:** `/plan-breakdown` on a TDD plan produces scoped tasks +
  a scoped gate + no triad, validates clean; review flags a deliberately over-broad scope, an
  overlapping-sibling pair, a test-including implementation scope, and a gate-less multi-leaf plan.
  Depends on M5. Size: M.
  Agent: `guardrails-skill-author`.
  filesTouched: `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`,
  `.claude/skills/guardrails-domain-knowledge/**`, `examples/**`.

- **M7 — Dogfood capstone.** Regenerate a real plan under the verified harness; run; confirm
  concurrent actions + serialized integrations + a fan-in through git→(AI?)→re-verify + the plan
  branch left with the user's branch untouched. Depends on M6 (+ Stage 3). Size: M.
  Agent: `guardrails-skill-author` (regenerate) + lead (runs).

**Dependency summary:** M1 → M2 → M3 → M4 (or M4a→M4b) → M5 → M6 → M7. M1 (de-serialize + channel
envelope + begin triad teardown) needs no git; M2 (real worktrees + reuse + triad teardown complete +
GR retirement, no integration) is useful for independent-task plans; M3 (scope check + matcher) is
self-contained on top of M2; **M4 (net-new lock + `IReVerifier` seam + FF/union integration + B1
atomic settle + resume + W-1) is the soundness core AND the false-green concentration — split if
schedule allows**; M5 (AI-merge merge-env-contract + scope field, consuming M4's `IReVerifier`) is the
PO's high-priority addition; M6/M7 are skills + dogfood. **Triad teardown spans M1+M2; the SSOT
triad-removal edits are a Stage-1 prerequisite (applied with the M1/M2 work, not after).**

---

## Schema changes (exact `02-schemas-and-contracts.md` edits)

Verbatim SSOT edits, applied in the milestone that implements each (contract-first). These EXTEND
plan 07's edits; where plan 07 and this plan both touch a section, **this plan's wording supersedes**.

### §1 Plan folder layout — plan branch + worktree root + git precondition + elevated `logs/`

Replace the plan-07 layout block and add:

> **Workspace must be a git repository top-level.** Parallel execution never writes the user's
> checkout. At run start the harness creates a **plan branch** `guardrails/<plan-name>` off the
> user's current HEAD and a **harness-owned integration worktree** on it; this is the sole merge
> target and the terminal-gate site for the run. Each task runs in a **segment worktree**: a linear
> chain **reuses one** segment worktree passed along the chain; a fan-out **inherits one** chain and
> **forks the rest** off the producer's committed tip; a fan-in **forks one** upstream and merges the
> others in. `runId` lives in worktree directory names and commit trailers, **not** the branch name.
> `guardrails validate` and a run pre-flight reject a non-git-top-level workspace (**`GR2015`**, a
> FRESH code — the old plan-07 draft cited `GR2013`, which is **taken on `master`** by the live triad
> `CaptureHashEscapesWorkspace`). The
> harness creates all worktrees under a **harness-owned root outside the workspace** — default
> `<temp>/guardrails-worktrees/<workspace-hash>/<runId>/`, overridable via `guardrails.json:
> worktreeRoot`. Worktrees + the plan branch are runtime state (wiped by `--fresh`, pruned on resume;
> the integration worktree is reattached, not pruned). The user's own working tree and branch are
> **read-only for the entire run**; the only optional write to the user's branch is `--merge-on-success`
> (§5.3). A `runOnCurrentBranch` opt-in makes the plan branch the current branch but still integrates
> via a harness-owned worktree, never the user's live checkout.

Change the `state/logs/...` layout line and add a sibling `logs/`:

```
plan-name/
├── guardrails.json
├── state/
│   ├── seed.json
│   ├── state.json
│   ├── run.json
│   └── merge-conflicts.log
├── logs/
│   └── <runId>/<task-id>/attempt-N/   # per-attempt artifacts (§8) — divided by runId, sibling of state/
└── tasks/ …
```

> The per-attempt log tree moves out of `state/` to a top-level `logs/` sibling, **divided by
> `runId`** (`logs/<runId>/<task-id>/attempt-N/`), so logs are findable and a re-run's logs never
> interleave with a prior run's. `state/` holds only harness-owned mutable run state; `logs/` is
> append-only audit. `--fresh` clears `logs/` for the abandoned run.

### §2 `guardrails.json` — `worktreeRoot` + `mergeOnSuccess` + `runOnCurrentBranch`; `maxParallelism` default 3

```jsonc
  "maxParallelism": 3,                // default 3 in worktree mode (chain-reuse keeps a linear chain to ONE tree)
```

```jsonc
  "worktreeRoot": null,               // OPTIONAL; override the git-worktree root. null = <temp>/guardrails-worktrees/<hash>/<runId>/
  "runOnCurrentBranch": false,        // OPTIONAL; if true the plan branch IS the current branch (still integrated via a harness-owned worktree)
  "mergeOnSuccess": false,            // OPTIONAL; if true AND the whole run goes green, merge plan branch guardrails/<plan-name> into the user's original branch at run end (ff-only when possible; AI-merge is NOT used here)
```

> - `worktreeRoot` overrides where the integration + segment worktrees are created. Each task's child
>   processes run with cwd = its segment worktree; the integration worktree (plan branch
>   `guardrails/<plan-name>`) is written only by the harness's integration (§5.3).
> - `runOnCurrentBranch` (default `false`) makes the plan branch the current branch instead of a fresh
>   `guardrails/<plan-name>`; the harness still integrates via a harness-owned worktree, never the
>   user's live checkout.
> - `mergeOnSuccess` (default `false`; CLI `--merge-on-success` overrides) opts into end-of-run
>   delivery of the plan branch into the user's original branch. **AI-merge is withheld at this
>   boundary** — a conflict, a failed post-merge re-verify, or a dirty user tree halts to `needs-human`
>   with the plan branch intact; never a force-overwrite, never an AI auto-resolve of the user's commits.
> - `maxParallelism` defaults to **3** because chain-reuse keeps a linear chain to one worktree; the
>   peak tree count is the DAG's max antichain width + the integration worktree. Drop to 2 on a
>   disk-constrained box; raise on a fast/large `worktreeRoot` volume.

### §3 `tasks/<id>/task.json` — REMOVE the triad/`exclusive`; ADD `integrationGate` + `writeScope`

- Delete `exclusive`, `captureHashes`, `restoreOnRetry`.
- Add to the jsonc block (after `dependsOn`):

```jsonc
  "integrationGate": false,    // optional, default false; marks a terminal whole-repo integration gate (§3.3)
  "writeScope": ["src/Foo/"],  // optional; the deterministic READ-ONLY write-scope check (§3.4). Absent ⇒ NO check.
                               //   every path the task's diff (git diff --name-status <taskBase>..<HEAD>)
                               //   adds/modifies/deletes/renames must be IN scope, or the task fails and
                               //   retries with feedback. The check NEVER reverts. Renames = paired D+A
                               //   (both in scope). A vacuous "**" / bare top-level dir is a granularity smell.
```

- Add **§3.3 "Terminal integration gate (`integrationGate`)"** — *carried from plan 07 §3.3*, with two
  clarifications: (a) the gate task's guardrails are exactly the run's **integration-guardrail set**
  (§4.3) — the `scope: "integration"` guardrails — run on the final HEAD; (b) **a multi-leaf or
  fan-in-bearing plan MUST declare exactly one `integrationGate` sink** (missing ⇒ `validate` error
  **GR2017**), and that sink **MUST carry ≥1 `scope: "integration"` guardrail** (empty integration set
  ⇒ `validate` error **GR2018**) — these are HARD errors because the terminal gate is the sole
  whole-repo soundness boundary for FF chains and AI-resolved unions (W-4).

- Add **§3.4 "Write-scope check (`writeScope`)"**:

> `writeScope` is an optional list of **workspace-relative path prefixes / globs** declaring the
> surface a task is permitted to add/modify/delete/rename. It drives a **deterministic, read-only
> harness check** (no revert, ever): after the task's action and **before** its own `guardrails/`,
> the harness computes `git diff --name-status <taskBase>..<segmentHEAD>` in the task's segment
> worktree and asserts every changed path satisfies `IsInScope(path, writeScope)`. A violation is a
> guardrail-class failure (retry with feedback naming the out-of-scope paths; eventual `needs-human`).
> **Absent ⇒ no check** (the off-switch — a task that can't be confidently scoped omits the field and
> is reported as a broad surface, never given a vacuous `**`). **Renames** are NOT detected via git
> `-M`; a rename presents as a paired **D + A**, and **both** paths must be in scope. **Deletions:**
> the deleted path must be in scope. The declared scope is also injected into the action prompt
> (advisory) — the deterministic check is the gate. `validate` rejects a scope entry that escapes the
> workspace (**GR2019**, error) and warns on a vacuous/over-broad scope (**GR2020**, warning;
> `plan-breakdown` should omit rather than emit a vacuous scope). **TDD
> test-protection:** a test-author task owns its test files in `writeScope`; the implementation task's
> `writeScope` EXCLUDES the test files, so the check deterministically enforces "the implementation may
> not write the tests" (the replacement for the `captureHashes`/`tests-untouched`/`restoreOnRetry`
> triad **that this same change deletes** — the triad is live on `master`, not already gone). The
> matcher (`IsInScope`/`Overlaps`/segment-matcher) is specified in full in plan 08 §2.1 (glob grammar,
> the 27-row truth table) and carries the §2.2 proof harness (the 27-row table + the two fuzz
> properties: membership-implies-overlap AND `Overlaps`-completeness). It is read-only, so a matcher
> bug can only false-red or miss-catch ONE task's own verdict — never write another task's files;
> `Overlaps` (the scheduler hint) retains cross-task reach and keeps the full fuzz rigor.

- Delete §3.1 (`captureHashes`) and §3.1.1 (`restoreOnRetry`) in full — **this change deletes the live
  triad** (it is present on `master`, not already gone) — and add the pointer note:

> *(Former §3.1/§3.1.1 — the `captureHashes`/`restoreOnRetry` triad — are **removed in this change**,
> along with the harness `CapturedFileStore`/`FileHashCapture`/`RestoreAncestorCaptures`/`WorkspaceLock`
> and the GR2013/GR2014 triad diagnostic meanings. Test files are now protected by (i) physical
> worktree isolation and (ii) the §3.4 write-scope check: an implementation task's `writeScope` excludes
> the test files, so an edit to them fails the deterministic check.)*

- Add **§3.2 "Worktree task semantics"** — *carried from plan 07 §3.2*, amended for reuse:

> The harness creates one integration worktree per run (plan branch `guardrails/<plan-name>`) — the
> sole merge target. Each task runs in a **segment worktree**: a linear chain reuses one segment
> worktree (the downstream task commits on top of the upstream's tip in the SAME tree — no inter-hop
> merge, no inter-hop re-verify, sound because no union is formed); a fan-out inherits one chain and
> forks the rest off the producer's committed tip; a fan-in forks one upstream and merges the others
> in (§5.3 union). A failed attempt does NOT discard the worktree — the harness `git reset --hard
> <taskBase> + git clean -fd` (preserving every upstream/sibling commit in the tree; `taskBase` is the
> task's start commit, distinct from the plan-branch `preHead`). A task that depends on another reads
> the producer's MERGED outputs (its worktree descends from the producer's committed tip). No
> cross-task `actionExitCode` channel exists. The user's checkout is never written; the plan branch's
> trailer-bearing commits (plain FF'd commits AND merge commits) are the durable resume record (§7).

### §4.3 — NEW: per-guardrail `scope` + the integration-guardrail set

Add a §4.3 "Guardrail scope (`scope: "integration" | "local"`)":

> A guardrail declares an optional `scope` (deterministic sidecar key §4.1, or prompt frontmatter
> §4.2): `"local"` (default) or `"integration"`. The run's **integration-guardrail set** = the union
> of all `scope: "integration"` guardrails across the plan (typically the whole-repo build + the
> whole test suite). At **every union point** (a fan-in or a non-FF plan-branch integration, §5.3), on
> the merged bytes, BEFORE the merge commit and BEFORE any downstream action, the harness re-runs (via
> the attempt-decoupled re-verify seam): (1) the union task's full guardrail set; (2) **every colliding
> sibling's FULL guardrail set — UNCONDITIONALLY, with NO touched-files filter** (the AI may have
> dropped a colliding sibling's contribution, leaving the sibling's test file untouched — a
> touched-files skip would miss exactly that); and (3) the integration-guardrail set. **The
> touched-files local-skip applies ONLY to a distant, NON-colliding task's `local` guardrails** (re-run
> only if the merge touched that task's files); it is **never** applied to a colliding sibling. The
> terminal `integrationGate` sink (§3.3) runs the **same** integration set on the final merged HEAD —
> the terminal gate and the per-union re-verify are one mechanism at two scopes. Because the re-verify
> runs on arbitrary union bytes outside any attempt lifecycle, it uses a **public attempt-decoupled
> re-verify seam** (NOT the attempt-bound internal guardrail runner). `plan-breakdown` marks the
> build/test guardrails `scope: "integration"`; `guardrails-review` flags an integration-sensitive plan
> with no integration-scoped guardrail (BLOCKER).

### §5.3 — replace with the FF / union integration + the unified atomic settle + AI-merge

Replace the §5.3 block (superseding plan 07's §5.3) with:

> **The harness writes only the harness-owned integration worktree (plan branch
> `guardrails/<plan-name>`), via integration, after a task's action and guardrails succeed in its
> segment worktree — and never otherwise. The user's checkout is read-only for the entire run.**
>
> There are two kinds of integration. **(A) Fast-forward** (a linear chain's commit, no sibling has
> advanced the plan branch): `git merge --ff-only` — **no new union, no re-verify** (the bytes already
> passed the task's guardrails in the segment worktree). **(B) Union** (a fan-in, or a non-FF
> integration where a sibling raced): a real merge that MUST be re-verified on the merged bytes before
> the commit.
>
> **Union resolution: git auto-merge → AI-merge → human.** `git merge --no-commit`; on conflict, the
> **AI-merge worker** (a constrained prompt behind `IPromptRunner`, §9.1) produces merged BYTES only,
> trusted via two **deterministic** checks — (i) no conflict markers remain (`git diff --check`),
> (ii) blast-radius: it modified only the git-reported-conflicted files (`git status --porcelain`); an
> out-of-bounds write or a remaining marker ⇒ discard (`reset --hard`) + needs-human. 1 retry. The AI
> resolves harness-internal unions only; it is **withheld** at the `--merge-on-success` user-branch
> boundary.
>
> **The verdict (identical for clean-auto and AI-resolved) is the deterministic re-verify:** re-run
> the union task's own guardrails + the run's **integration-guardrail set** (§4.3) on the `--no-commit`
> merged bytes, then assert `git status --porcelain` shows only the staged merge (W3 read-only check).
> Any re-verify fail / remaining conflict / dirtied tracked file ⇒ `git reset --hard preHead`;
> `needs-human`; write no fragment, consume no `mergeSequence`. AI-merge + its re-verify run in the
> fan-in's **private forked worktree OFF the serialize lock**; only the integration of the verified
> result into the plan branch is **under the lock**, with a staleness re-verify against the current
> plan-branch bytes.
>
> **The atomic settle (state + git + journal as one ordered unit, under the serialize lock).** On
> success, in this FIXED order: (1) deep-merge the task's fragment into `state.json`; (2) `git commit`
> the integration (the FF move for case A, the merge commit for case B) carrying the parseable
> `Guardrails-Task: <taskId>` / `Guardrails-Run: <runId>` trailer — **written on the plain FF'd commit
> as well as on merge commits**, so resume can read FF integrations (§7); (3) consume the
> `mergeSequence` + journal `Succeeded`. The fragment merge precedes the commit so the resume pre-pass
> can never treat a task succeeded-by-commit while its state is missing. Every non-success path is a
> single `git reset --hard preHead` (NOT `merge --abort`, which fails rc=128 on the dirtied-tracked
> path) — leaving state, git, and journal all UNCHANGED, never half-merged, and the user's checkout
> untouched. A git/IO failure during integration is a `needs-human` halt routed through the normal
> failed path, never an uncaught throw.
>
> **Retry preserves upstream work:** a failed attempt is `git reset --hard <taskBase> + git clean -fd`
> in its segment worktree (keeping every upstream/sibling commit; `taskBase ≠ preHead`), not a
> discard-and-recreate.
>
> **Run end (opt-in delivery).** When the run drains wholly green AND `mergeOnSuccess`/
> `--merge-on-success` is set, the harness merges the plan branch into the user's original branch
> (ff-only when possible, else a real merge whose re-verify must pass). **AI-merge is NOT used here.**
> A conflict / failed re-verify / dirty user tree halts to `needs-human`, plan branch intact — never a
> force-overwrite. Default OFF leaves the plan branch for the user to review and merge.

Update the §5.3 trailing sentence ("Any new capability that needs the harness to write workspace
files…") to point at this integration (plus the opt-in end-of-run merge) as the sole exceptions.

### §9.1 — NEW: the AI-merge worker

Add a §9.1 "AI-merge worker":

> The AI-merge worker resolves a git merge conflict during a union (§5.3 case B). It is a **constrained
> prompt action behind `IPromptRunner`** (the same seam as `claude`). **The existing `IPromptRunner`
> contract returns metadata only** (`PromptResult` = `{Completed, IsError, ResultText, CostUsd,
> NumTurns, Summary}`) — **there is no byte channel.** So the worker uses the existing **on-disk file
> convention** (the runner writes a file, the harness reads it) via a **NEW merge env contract**, and a
> **distinctly named merge prompt profile** (NOT a `guardrailOverrides`-shaped profile — that is a
> guardrail-verifier concept). **It is a BYTE PRODUCER, never a VERDICT PRODUCER:**
> - **Merge env contract (new):** `GUARDRAILS_MERGE_BASE`, `GUARDRAILS_MERGE_OURS`,
>   `GUARDRAILS_MERGE_THEIRS` (the three-way inputs on disk) and `GUARDRAILS_MERGE_OUT` (the path the
>   worker writes the resolution to). The harness reads `GUARDRAILS_MERGE_OUT` after the run.
> - **Input:** the conflicted files (with markers) + base/ours/theirs on disk, and the colliding
>   upstream tasks' intents (their `task.description` + `writeScope`) composed into the prompt string.
> - **Output:** the merged bytes only, written to `GUARDRAILS_MERGE_OUT`. A rationale is logged
>   (NON-gating, never read as a verdict). `PromptResult.IsError` and the exit code are **not** the
>   verdict.
> - **Trust:** two deterministic checks — no conflict markers remain (`git diff --check`); blast-radius
>   (modified only the git-reported-conflicted files, `git status --porcelain`). A violation ⇒ discard
>   (`reset --hard`) + `needs-human`.
> - **Budget:** 1 retry (2 attempts). Escalate to `needs-human` on markers-left / out-of-bounds /
>   re-verify-fail / budget. The AI's exit code is never a verdict.
> Its cost is charged against `maxCostUsd` like any prompt attempt. It is configured under
> `promptRunners` as a **reserved merge runner profile** (e.g. `ai-merge`) — a distinct merge profile
> named for what it is (read the conflict, write only `GUARDRAILS_MERGE_OUT`), **not** a
> `guardrailOverrides` block.

### Diagnostic codes (`DiagnosticCodes.cs`) — VERIFIED against the live file, NOT a superseded plan

**Reality (must be re-confirmed at implementation time):** the live `DiagnosticCodes.cs` has
**`GR2013` = `CaptureHashEscapesWorkspace`** and **`GR2014` = `RestoreOnRetryWithoutCaptureHashes`** —
**BOTH taken by the triad, which is still on `master`.** The highest live validation code is GR2014.
Plan 07's SSOT edits were **never applied**. Therefore:

- **RETIRE GR2013/GR2014's triad meanings** in the same change that deletes the triad (M2). There is no
  "reuse the freed number" — the numbers are not freed until this plan frees them. After retirement,
  either remove the two constants or repurpose them with a code comment recording the retirement.
- **All new gates get FRESH numbers GR2015+**, assigned canonically as follows (this block is the SSOT
  for the allocation; every other mention in this doc must match it):

| Code | Severity | Meaning |
|---|---|---|
| **GR2015** | error | workspace is not a git repository top-level (run pre-flight + `validate`) |
| **GR2016** | warning | Windows MAX_PATH risk (deep worktree root + deep source) |
| **GR2017** | error | a multi-leaf or fan-in-bearing plan declares no terminal `integrationGate` sink (W-4 — the terminal gate is the sole whole-repo soundness boundary, so its absence is HARD) |
| **GR2018** | error | an `integrationGate` sink carries no `scope: "integration"` guardrail (empty integration set — the gate would verify nothing) |
| **GR2019** | error | a `writeScope` entry escapes the workspace root (`../…`, absolute, drive-rooted) |
| **GR2020** | warning | a vacuous/over-broad `writeScope` (`**` or a bare top-level dir); narrow or omit it (broadness is a smell, not a hard fault) |

---

## Implementation handoff (agent + filesTouched + sequencing)

Sequenced; later milestones depend on earlier (detail in §Milestones).

| Stage / Milestone | Agent | filesTouched (primary) |
|---|---|---|
| Stage 1 — SSOT contract (+ **triad-removal as a Stage-1 prerequisite**) | `guardrails-architect` proposes, lead applies | `docs/plans/02-schemas-and-contracts.md` (§1 plan branch + reuse topology + elevated `logs/`; §2 `worktreeRoot`/`runOnCurrentBranch`/`mergeOnSuccess`/`maxParallelism` 3; §3 **remove triad** `exclusive`/`captureHashes`/`restoreOnRetry` + delete §3.1/§3.1.1, add `integrationGate`+`writeScope`; §3.2 worktree-reuse semantics; §3.3 gate + GR2017/GR2018; §3.4 write-scope check + GR2019/GR2020; §4.3 guardrail `scope`; §5.3 FF/union integration + atomic settle + AI-merge merge-env-contract; §9.1 AI-merge worker; §8 logs; **retire GR2013/GR2014 triad meanings, add FRESH GR2015–GR2020**), this doc |
| M1 — de-serialize + per-task channel envelope + handle seam + BEGIN triad teardown | `guardrails-harness-developer` + `guardrails-test-author` | `Execution/IWorktreeProvider.cs`, `WorktreeHandle.cs`/`IntegrationHandle.cs`, `FakeWorktreeProvider.cs`, `ITaskExecutor.cs` (handle param), `TaskExecutor.cs` (drop `RestoreAncestorCaptures`), `Scheduler.cs` (channel envelope, drop `exclusive` gate), `tests/**` (all fakes re-signed) |
| M2 — real worktrees + REUSE/CHAINING topology + plan branch + triad teardown part 2 + GR retirement + logs elevation | `guardrails-harness-developer` + `guardrails-test-author` | `Execution/GitWorktreeProvider.cs`, `Execution/WorktreeManager.cs` (recorded-sha fork), `TaskExecutor.cs`, `Scheduler.cs`, `WorkspaceLock.cs` (**deleted**), `Loading/PlanValidator.cs` (delete 2 triad validators; add gate/git/MAX_PATH), `Loading/DiagnosticCodes.cs` (**retire GR2013/2014; add GR2015/2016/2017/2018**), `Model/TaskNode.cs`, `Model/RunConfig.cs`, `State/` log paths, SSOT §1/§2/§3/§8, `tests/**` (triad-ref re-baseline + `StatePlanBuilder` fakes) |
| M3 — write-scope CHECK + matcher (fresh to §2.1) + 27-row table + 2 fuzz properties | `guardrails-harness-developer` + `guardrails-test-author` (table FIRST, red-on-naive-matcher) | `Execution/WriteScope.cs` (new), `Execution/WriteScopeCheck.cs`, `TaskExecutor.cs`, `Model/TaskNode.cs`, `Loading/PlanValidator.cs` (GR2019/GR2020), `Loading/DiagnosticCodes.cs`, SSOT §3.4, `tests/**` (truth table + 2 fuzz props) |
| M4 — **net-new merge lock + NEW `IReVerifier` seam** + FF/union integration + atomic settle (B1) + resume (FF wrinkle + W-1) + reset-retry + `--merge-on-success` (split M4a/M4b if schedule allows) | `guardrails-harness-developer` + `guardrails-test-author` (tests FIRST) | `Execution/IReVerifier.cs` (**new, public, attempt-decoupled**), `Execution/Integrator.cs`, `TaskExecutor.cs` (`MergeAndSettleAsync`, reset-retry), `AttemptJournaler.cs`, `Scheduler.cs` (**net-new `SemaphoreSlim(1,1)`** + resume W-1), `Model/RunConfig.cs`, `Guardrails.Cli`, `State/RunReset.cs`, SSOT §4.3/§5.3, `tests/**` |
| M5 — AI-merge worker (**NEW merge env contract + distinct profile**) + per-guardrail `scope` filter, **consuming M4's `IReVerifier`** | `guardrails-harness-developer` + `guardrails-test-author` (fake AI runner writes `_OUT` FIRST) | `Execution/AiMergeResolver.cs`, `Execution/Integrator.cs`, `Execution/IReVerifier.cs` (scope filter), `Model/GuardrailNode.cs`, `Model/PromptRunnerConfig.cs` (merge profile), `Loading/PromptFileParser.cs`/sidecar, SSOT §4.3/§9.1, `tests/**` |
| M6 — skills switch-over | `guardrails-skill-author` | `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`, `.claude/skills/guardrails-domain-knowledge/**`, `examples/**` |
| M7 — dogfood capstone | `guardrails-skill-author` regenerate, lead runs | a regenerated plan folder; run journal |
| Stage 3 — independent verification | `guardrails-devils-advocate` + lead | (review only) |

---

## Proposed plan-document edits

I propose (you approve, then I apply):

1. **`docs/plans/08-parallel-execution.md`** — this document (written to the worktree, **not
   committed**).
2. **`docs/plans/02-schemas-and-contracts.md`** — the §Schema-changes above, applied verbatim in the
   same change as the M1–M5 harness work begins (Stage 1), including the **triad-removal edits as a
   Stage-1 prerequisite** (delete §3.1/§3.1.1 and the `exclusive`/`captureHashes`/`restoreOnRetry`
   fields) and the **GR2013/GR2014 triad-meaning retirement + the fresh GR2015–GR2020 allocation**.
   Until then they live here as the spec. Note: plan 07's SSOT edits were never applied, so this is the
   first change to land them — verify the live SSOT state before editing.
3. **`docs/plans/07-worktree-per-task.md`** — add a banner at the top: *"SUPERSEDED by plan 08
   (parallel-execution). Plan 08 preserves this plan's soundness core (worktree isolation, two-phase
   merge + re-verify, the unified atomic settle, `reset --hard` rollback, resume-by-trailer, the
   terminal gate, honest halts) and extends it with worktree reuse/chaining, the write-scope CHECK
   (disjoint-scope reborn read-only), AI-merge as a v1 byte-producer, and the elevated logs layout.
   Read plan 08 for the design of record; this plan remains for its detailed soundness derivations."*
4. **`docs/plans/03-roadmap.md`** — promote v2 bet #1 to **active**, pointing at plan 08; **move #57
   (AI merge-conflict resolution) INTO v1** (it is now the §4/§9.1 AI-merge worker, byte-producer +
   deterministic checks + re-verify); keep #54 (mechanics) as active; add a v2 sub-bet for **CoW /
   sparse / worktree-pooling disk mitigations** (deferred from the PO's v1 ask with the technical
   reasons in §Honest costs) and for **re-basing the plan branch before `--merge-on-success`
   delivery** (counter-6 tightening). Rewrite risk-register item #2 to: "each task runs in a git
   worktree (reused along linear chains); the harness integrates into a plan branch
   `guardrails/<plan-name>` (the single writer; FF for linear, re-verified union for fan-in/sibling
   races); a write-scope CHECK keeps diffs small (read-only, never reverts); conflicts go git→AI→human;
   the user's checkout is never written." Drop the `exclusive`-by-default mention. Note the
   worktree-mode `maxParallelism` default of 3. Record the rejected *shared-workspace* disjoint-scope
   attempt (false-greened twice; its plan docs live only on the abandoned `feat/disjoint-scope-ownership`
   branch, NOT in this tree), with its write-scope glob matcher salvaged read-only and **re-specified
   self-contained in plan 08 §2.1** (so no future reader needs the abandoned branch).

No code, no commits — design only.
