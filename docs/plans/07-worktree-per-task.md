# Architecture: Worktree-per-task parallelism

> **Status:** design-of-record for the parallel-execution feature. This is the input to
> `/plan-breakdown` once approved. It is the **course-correct away from disjoint-scope /
> shared-workspace** (plans 05 + 06), which is abandoned — see "Why we're here". Promotes
> `03-roadmap.md` v2 bet #1 (worktree-per-task) to **active** and records plans 05/06 as the
> rejected disjoint-scope attempt. The contract additions land in `02-schemas-and-contracts.md`
> (the SSOT) in the same change that implements them.
>
> **One-line claim, scoped honestly:** worktrees give *true mutual isolation* of concurrent
> tasks for free (separate working trees on a shared object store); a clean **merge alone is NOT
> sufficient** — guardrails MUST re-run on the merged tree. The atomic unit is **the whole tail —
> state-fragment merge + git merge-back + post-merge re-verify + journal `Succeeded` — committed
> together or rolled back together, all under the single serialize-merges lock.** Nothing is
> journaled `Succeeded`, no fragment reaches `state.json`, and no `mergeSequence` is consumed until
> the merged bytes have passed re-verify on the integration worktree. Conflicts halt to needs-human;
> **no AI auto-resolve in v1.**
>
> **The merge target is a harness-owned integration worktree, never the user's checkout.** At run
> start the harness creates a dedicated *integration worktree* on a fresh run branch
> `guardrails/run-<runId>` (off the user's current HEAD). ALL merge-back and post-merge re-verify
> happen there; the user's actual checkout is **read-only for the entire run** and is never written.
> At run end the run branch is left for the user to review and merge — an optional, default-OFF
> `--merge-on-success` fast-forwards it into the user's original branch only if the whole run went
> green. This dissolves most of the "pristine-primary" precondition (counter-2) and gives resume a
> clean durable record (the run branch's merge commits; §6).

---

## What's being asked

Replace the shared-workspace parallel-execution mechanism with **one git worktree per task**.
Each task runs its action + guardrails inside its own worktree (created off the **integration
worktree's** HEAD — the run branch); on success the task's commit is **merged back** into the
integration worktree under a serialize-merges lock; a merge **conflict** (or a failed post-merge
re-verify) **halts the task to `needs-human`** with the worktree preserved for inspection, blocks
its dependents, and lets independent branches continue. The product owner's instruction is
explicit: **lean on git — the battle-tested isolation + merge primitive — rather than
re-implement a transaction manager.**

**The user's checkout is never the merge target.** A dedicated **integration worktree** — a second
harness-owned worktree on a fresh run branch `guardrails/run-<runId>`, created off the user's HEAD
at run start — is where every merge-back lands and where every post-merge re-verify runs. The
user's own working tree and branch are **read-only for the whole run**. Downstream per-task
worktrees are cut off the integration worktree's HEAD, so they physically see upstream's merged
outputs exactly as before — the only change is *which* tree accumulates the merges (a disposable
harness-owned one, not the user's live checkout).

**Ambiguity named & narrowed.** Five points the brief leaves open; I narrow each here so
implementation does not stall, and call out the one I am escalating rather than deciding:

0. **Merge target (SETTLED by the product owner).** The merge target is a dedicated
   **integration worktree** on run branch `guardrails/run-<runId>`, off the user's HEAD at run
   start — NOT the user's live checkout. All merge-back + re-verify land there; the user's tree is
   read-only for the whole run; an optional default-OFF `--merge-on-success` merges the run branch
   into the user's original branch at run end only if the whole run went green. This is no longer
   an open question — it is the design's center (§Design 2, §Design "the worktree root").

1. **Where post-merge re-verification runs (the load-bearing soundness question).** The brief
   offers "decide where post-merge re-verification happens (or whether guardrails-in-worktree +
   a clean merge is sufficient)." **Narrowing: a clean merge is NOT sufficient; guardrails
   re-run on the merged integration worktree, and merge+re-verify is one atomic unit** (§Design 2).
   A git merge that reports *no textual conflict* can still produce bytes that fail the build (a
   semantic/logical conflict — two tasks edit different lines of the same method, or one renames
   a symbol the other calls). In-worktree guardrails verified the *pre-merge* bytes; the bytes
   that *ship* are the *post-merge* bytes. Verifying anything other than the bytes that ship is
   exactly the class of false-green that sank plan 05. This is the most consequential narrowing
   in the document.

2. **`writeScope`'s fate (SETTLED by the product owner).** **Drop it entirely in v1** (§Design 5).
   Physical isolation makes per-task write-enforcement unnecessary; keeping `writeScope` as a
   "merge contract / review hint" is a YAGNI hedge that re-imports the glob-matcher complexity the
   product owner called "a self-implemented likely-to-have-bugs solution." The product owner has
   confirmed the drop; §Design 5 records the rationale and the rejected lightweight-optional form.

3. **Conflict resolution.** **MOOT by decree:** conflicts → human in v1. No AI resolver (that
   was v2-bet-#1's #57 sub-issue; it stays v2). This removes the third of plan 05 §1's four
   original worktree blockers.

4. **Whether git is required.** **Narrowing: yes — a validation gate.** The workspace must be a
   git repository top-level. A non-git workspace is a `validate`/run error (new GR code), not a
   silent fallback to shared-workspace (which would re-import every plan-05 hazard through a back
   door). Honest-halt over silent degrade.

5. **The merge-back lock granularity vs. true parallel throughput.** Merges into the integration
   worktree serialize; the *actions* (the expensive part — prompt agents) run fully concurrent. I
   narrow the lock to **the merge+re-verify critical section only**, never held across an action.
   The honest cost (re-verify throughput under a serialized tail) is named in §Honest costs, not
   hidden — that is the discipline plan 05 violated.

6. **Default `maxParallelism` for worktree mode (SETTLED).** Each concurrent task is a full
   working tree on disk, so the naive default-4 (or higher) would create four multi-GB trees out
   of the box. **Narrowing: worktree mode defaults `maxParallelism` to 2** — a conservative
   number that still demonstrates real concurrency (two overlapping actions, one serialized merge
   tail) without an N×multi-GB surprise on a large repo. Documented in the SSOT §2 and §Honest
   costs. The user raises it explicitly when their repo/disk affords it.

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

| Concern | Placement |
|---|---|
| Worktree lifecycle (create / run-inside / discard / merge-back), `IWorktreeProvider` seam | **harness** — `Execution/` (new `WorktreeManager` + `IWorktreeProvider`), `Scheduler.cs`, `TaskExecutor.cs` |
| **Integration worktree** (run-branch `guardrails/run-<runId>` off the user's HEAD at run start; the sole merge target) | **harness** — `WorktreeManager` (creates it at run start), `Scheduler.cs` (merges into it) |
| Merge-back + conflict-halt + post-merge re-verify atomicity (all against the **integration worktree**) | **harness** — new `MergeBack` collaborator + `Scheduler` serialize-merges lock |
| **B1 — unify the state-fragment merge + `mergeSequence` + `Succeeded` journal into the merge-back critical section** (the atomic settle tail) | **harness** — `TaskExecutor.MergeAndSettleAsync` + `AttemptJournaler` refactor; `Scheduler` holds the lock |
| **B2 — terminal integration gate** (`integrationGate` task field; multi-leaf validation; run-green contingent on the gate passing the final HEAD) | **harness + schema** — `TaskNode.IntegrationGate`, `PlanValidator` (GR2015), `Scheduler` final-HEAD re-verify; SSOT §3/§3.3 — and **skill** (`plan-breakdown` emits, `guardrails-review` flags) |
| **`--merge-on-success`** (default OFF; merge run branch → user's original branch at run end iff whole run green) | **harness** — `Cli` flag + `RunConfig` (`mergeOnSuccess`); end-of-run hook in `Scheduler`/run command |
| Git-required validation gate; new GR codes | **harness** — `Loading/PlanValidator.cs`, `Loading/DiagnosticCodes.cs` |
| Replacing `exclusive`/`WorkspaceLock` serialization | **harness** — `Scheduler.cs`, `Model/TaskNode.cs`, loader |
| Dropping `writeScope` (never shipped on master) | **schema (no-op)** — confirm it is not in the SSOT; nothing to remove from master |
| `--fresh`/`reset` wiping the worktree root (per-task trees AND the integration worktree); resume orphan pruning | **harness** — `State/RunReset.cs`, scheduler resume pre-pass |
| **Resume reconciliation** (run-branch merge-commit **trailers** are the durable record; B3 — second-parent cross-check dropped; §6) | **harness** — scheduler resume pre-pass reads `guardrails/run-<runId>` first-parent merge-commit `Guardrails-Task:` trailers |
| Worktree task semantics; integration-worktree merge-back §5.3 write exception; `--merge-on-success`; git-required GR codes; `GUARDRAILS_STATE_IN` downstream contract (already shipped) | **schema** — `02-schemas-and-contracts.md` §1, §3, §5.3, §2 |
| `plan-breakdown` / `guardrails-review` doctrine (no `writeScope`; downstream reads upstream's MERGED outputs) | **skill** — `.claude/skills/**` |
| Roadmap: bet #1 → active; plans 05/06 recorded as rejected | **docs** — `03-roadmap.md` |
| **AI merge-conflict resolution** (the resolver sub-agent) | **v2** — explicitly NOT v1 (roadmap bet #1's #57 half) |
| **Worktree pooling / copy-on-write / sparse checkout for disk** | **v2 / out of scope** — named in §Honest costs as the disk mitigation if v1's cost proves unacceptable |

---

## Invariants in play

The design **strengthens** invariant 2 (the one the shared-workspace approach strained worst)
and respects the rest. Named, with how each is respected or strained:

1. **Deterministic guardrails over prompt-judges; judges never alone.** Untouched in spirit, but
   *sharpened*: the merge-back's correctness check is the task's **own deterministic guardrails
   re-run on merged bytes** — not a new prompt-judge, not git's own "no conflict" signal. Git's
   clean-merge result is treated as *necessary-not-sufficient*; the deterministic guardrails are
   the verdict. (This is the #1 lesson: a deterministic *signal* — "git found no conflict" — with
   the wrong *semantics* is still wrong, exactly as plan 06 §Invariants noted about #88.)

2. **Harness is the single writer of merged state; children get snapshots, write fragments.**
   **This invariant is the design's center, and worktrees fit it far better than the
   shared-workspace hack did — and the integration worktree sharpens it further.** Each task's
   child writes only *its own worktree* — a private working tree it cannot use to corrupt a
   sibling (physical isolation; #88-class corruption is structurally impossible, not "reverted
   post-hoc"). The harness, under the serialize-merges lock, is the **single writer of the
   integration worktree** (run branch `guardrails/run-<runId>`) via `git merge` — the workspace
   analogue of "single writer of merged state." Critically, the **user's own checkout is written by
   nobody during the run** (read-only) — so "single writer" is now true against a tree no other
   actor (not even the user's editor) touches, which is *stronger* than writing the user's live
   primary. The plan-05 enforcer *violated the spirit* of this (acting for task A, it deleted
   files task B produced); worktrees restore it by construction. **State merge is NOT unchanged —
   this is the B1 correction.** The fragment deep-merge, the `mergeSequence` consumption, and the
   `Succeeded` journal write move OUT of the executor's in-worktree success path and INTO the
   merge-back critical section, after re-verify passes (§Design 2). The harness is still the single
   writer of merged state, but now state, git, and journal are committed as one serialized tail so
   a failed re-verify never leaves a task journaled `Succeeded` with a fragment its integration HEAD
   does not reflect (the split-brain the prior draft waved past with "harness still deep-merges
   fragments, unchanged"). (The one exception — `--merge-on-success` writing the user's branch at
   run end — is a single explicit, opt-in, post-run fast-forward, not a mid-run write; §Design
   "merge-on-success".)

3. **Verdicts come from files, never CLI exit codes.** Respected, with a git-specific corollary
   that must be enforced in code: **`git`'s own exit codes ARE consumed** (e.g. `git merge`
   returns non-zero on conflict) — but that is the harness reading a *tool's* result to drive its
   own control flow, NOT a guardrail verdict. The *task's* pass/fail still comes from its
   guardrail outcomes (deterministic exit / prompt verdict file), re-run on merged bytes. A merge
   conflict is a harness-level "cannot proceed → needs-human," routed through the existing
   `needs-human` machinery, never an uncaught throw (the plan-06 §2.5 lesson: a git/IO failure is
   an actionable verdict, not a process crash).

4. **`02-schemas-and-contracts.md` is the schema SSOT — a contract change lands there in the SAME
   change.** Respected: the §Schema-changes block below is the verbatim SSOT edit set, applied in
   the milestone that implements it (M2/M3), never after.

5. **Honest halts — nothing marked done unverified; needs-human is a feature.** Respected and
   *extended*: a merge conflict, a failed post-merge re-verify, and a non-git workspace are all
   honest halts. The integration-worktree model also makes the *partial* outcome honest: a run that
   halts mid-way leaves a run branch `guardrails/run-<runId>` with only the merge commits that
   actually passed re-verify, and `--merge-on-success` refuses to touch the user's branch unless the
   *whole* run went green — so a partially-green run never silently advances the user's branch. The
   capstone dogfood is a **demonstration after independent verification**, not the proof (the
   plan-06 §9 discipline, carried verbatim into §Process).

6. **Plain files, light setup — no databases, daemons, or SaaS in v1.** Respected: git is the
   only added dependency, and git is *already* a universal local tool (no daemon, no service).
   The worktree root and baselines are plain files under `state/`. The one honest tension: git
   becomes a **hard requirement** (§Honest costs) — "light setup" now means "have git," which is
   a reasonable bar for the tool's audience but is a real new precondition, not zero.

---

## Design

### Worktree lifecycle (the single seam: `IWorktreeProvider`)

A new `IWorktreeProvider` seam isolates every git invocation behind one interface — the same
discipline that quarantines the Claude CLI behind `ClaudePromptRunner`. The scheduler/executor
depend on the *interface*, never on shelling `git` directly, so:
- M1 can ship a **fake** in-memory/temp-dir provider that proves the scheduler de-serializes
  *without git at all* (the walking skeleton), and
- every git flag spelling lives in exactly one `GitWorktreeProvider` class (the §3(b)
  CRLF/byte-identity discipline and the §2 atomicity flags all live there).

```csharp
public interface IWorktreeProvider
{
    // Run start: create the integration worktree on a fresh run branch
    // guardrails/run-<runId>, off the USER's current HEAD. The sole merge target.
    IntegrationHandle CreateIntegration(string runId, CancellationToken ct);
    // Off the INTEGRATION worktree's HEAD (the run branch); root OUTSIDE the workspace.
    // Branch is per-ATTEMPT: guardrails/<runId>/<taskId>/<attempt> (B3 — distinct per retry).
    WorktreeHandle Create(string taskId, int attempt, IntegrationHandle integration, CancellationToken ct);
    // git merge --no-ff <per-attempt-branch> into the INTEGRATION worktree. Called BY the executor's
    // MergeAndSettleAsync, with the scheduler holding the serialize-merges SemaphoreSlim(1,1).
    MergeResult MergeBack(WorktreeHandle handle, IntegrationHandle integration, CancellationToken ct);
    // git worktree remove --force; idempotent (safe on an already-gone tree).
    void Discard(WorktreeHandle handle);
    // git worktree prune + remove orphans not in the live set (resume / crash recovery).
    // Never removes the integration worktree of a resuming run.
    void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integration);
    // Run end, opt-in: fast-forward/merge the run branch into the user's original branch
    // ONLY if the whole run went green. No-op when --merge-on-success is off (the default).
    MergeOnSuccessResult MergeRunBranchIntoUserBranch(IntegrationHandle integration, CancellationToken ct);
}
```

`WorktreeHandle` carries the worktree's absolute path, its **per-attempt** branch name
(`guardrails/<runId>/<taskId>/<attempt>` — distinct per attempt so retries never collide on a
branch name, the resume walk and `--fresh` deletion stay coherent; B3), and the integration HEAD
sha it was created off. `IntegrationHandle` carries the integration worktree's absolute path, its
run-branch name (`guardrails/run-<runId>`), the user's **original** branch name + HEAD sha
(captured at run start, load-bearing for `--merge-on-success` and for detecting user-branch
divergence; §Devil's-advocate counter 6), and the run-branch HEAD it was created off (the §2 atomic
reset rolls back to a sha on the *run branch*, not the user's branch).

**Run-start (once per run), before any task:**

0. **Create the integration worktree.** `git worktree add <root>/_integration
   guardrails/run-<runId> <userHead>` — a fresh run branch off the user's current HEAD. This is
   the sole merge target for the whole run. The user's original branch name + HEAD sha are
   captured into the `IntegrationHandle` (for `--merge-on-success` and divergence detection). The
   user's checkout is **never touched again** until (optionally) the end-of-run merge.

**Lifecycle per task attempt:**

1. **Create.** `git worktree add <root>/<taskId> <integrationHead>` on a fresh **per-attempt**
   branch `guardrails/<runId>/<taskId>/<attempt>`, **off the integration worktree's HEAD** (the run
   branch), so the
   per-task tree already contains every sibling merged so far. The task's **workspace becomes the
   worktree path** — this is the one change to `TaskExecutor.ResolveWorkingDirectory` / the `cwd`
   it hands `ProcessRunner` and the env contract (`GUARDRAILS_PLAN_DIR` stays the plan folder; the
   *workspace cwd* is redirected to the worktree). Crucially, the plan folder (`state/`, task
   folders) is NOT inside any worktree — it lives with the plan dir alongside the user's checkout —
   so a worktree checkout never disturbs harness state. (See "the worktree root" for why worktrees
   are created off a sibling root.)

2. **Run.** Action + guardrails run inside the worktree exactly as today — same `ProcessRunner`,
   same env, same guardrail pass. **No mechanism changes here; only the cwd moves.** Because the
   tree is physically separate, a flailing agent writing `../../sibling/x` either escapes into a
   real sibling dir (caught by the existing nothing — see §Honest costs "escape" limit) or stays
   inside its own tree (the common case). There is **no scope enforcer, no revert, no glob
   matcher** — the isolation is the filesystem's, not the harness's.

3. **Discard on failure/retry.** A failed attempt's worktree is removed (`git worktree remove
   --force`) and a fresh one is created off the *current* integration HEAD for the retry — so a
   retry starts pristine *and* picks up any siblings that merged in the interim (the "fix, don't
   restart" feedback is injected as today; the workspace is clean by construction, not by
   restore). On a *terminal* failure (budget exhausted → `needs-human`), the worktree is
   **preserved** (NOT discarded) for human inspection (§2).

4. **Merge-back on success** — into the integration worktree, §2.

### 2. Merge-back, made atomic (resolves plan-05 blocker #1)

This is the heart of the design and the place the prior approach's false-green lesson bites
hardest. The merge-back is a **critical section under a single serialize-merges lock** (one
merge at a time into the **integration worktree** — see §4).

**The atomic unit is the WHOLE tail, not "merge + re-verify."** This is the B1 correction: in the
shared-workspace harness the executor's success path writes `state.json` (deep-merges the task's
fragment), consumes a `mergeSequence`, and journals `Succeeded` *before* any merge-back could run.
Bolting a worktree merge-back on after that re-introduces the plan-05 disease through a new seam: a
failed post-merge re-verify would roll back **git** (`reset --hard preHead`) but leave the task
journaled `Succeeded` with its fragment in `state.json` — a downstream task then reads state keys
the integration HEAD does not reflect. **So the fragment merge, the `mergeSequence` consumption,
and the `Succeeded` journal write MUST move out of the executor's in-worktree success path and into
this critical section, AFTER re-verify passes.** All four effects (state, git, journal,
sequence) are committed together on the success path or none are committed on any failure path.
Every `git` invocation below targets the **integration worktree** (`<integ>` = the integration
worktree path on run branch `guardrails/run-<runId>`) — the user's checkout is never read or
written here:

```
acquire serialize-merges lock            # one merger at a time
  preHead = git -C <integ> rev-parse HEAD          # rollback point ON THE RUN BRANCH
  commit the worktree's changes on its task branch # (harness commits; agent never does)
  # Merge into the WORKING TREE but DO NOT create the merge commit yet (--no-commit):
  result = git -C <integ> merge --no-commit --no-ff guardrails/<runId>/<taskId>/<attempt>
  # ROLLBACK on EVERY non-success path is `git reset --hard preHead` (restores index + worktree AND
  # clears MERGE_HEAD in all cases). NOT `merge --abort`: it FAILS rc=128 "Entry not uptodate" when a
  # re-verify dirtied a tracked file the --no-commit merge had staged. Any non-zero from the rollback
  # itself -> needs-human.
  if result == CONFLICT:
     git -C <integ> reset --hard preHead                  # integration worktree returns to preHead
     -> HALT task needs-human (worktree preserved); DO NOT write fragment; DO NOT consume
        mergeSequence; journal needs-human; block dependents; release lock
  else:  # clean textual merge in the working tree — necessary, NOT sufficient; NO commit exists yet
     reRun = run THIS task's guardrails against the INTEGRATION WORKTREE (post-merge bytes)
     if NOT reRun.AllPassed:
        git -C <integ> reset --hard preHead                # discard the staged-but-uncommitted merge
        -> HALT task needs-human ("merged cleanly but failed re-verify on the integration tree");
           DO NOT write fragment; DO NOT consume mergeSequence; journal needs-human;
           worktree preserved; block dependents; release lock
     else:
        assert git -C <integ> status --porcelain shows ONLY the expected staged merge, no
               unexpected tracked modification                                    # W3
        if dirtied-tracked: git -C <integ> reset --hard preHead   # merge --abort FAILS here (rc=128)
           -> HALT needs-human ("re-verify dirtied a tracked file"); no fragment; no sequence
        # --- the settle tail, in this FIXED order (crash-window rule below) ---
        (1) write THIS task's fragment into state.json    # BEFORE the durable merge commit exists
        (2) git -C <integ> commit  (creates the merge commit + Guardrails-Task: trailer = the
                                     resume authority) — this is the single commit point
        (3) reserve+consume the mergeSequence; journal Succeeded
        -> task SUCCEEDS; merged bytes are the bytes that were verified; release lock
release lock
```

**Is this trio ACTUALLY atomic? No — and the design must not pretend it is.** There is no shared
transaction across three persistent stores (git ODB/refs, `state.json`, `run.json`). What makes it
*sound* is not atomicity but **a single serialized commit order plus one durable authority the
resume pre-pass reconciles to**. The load-bearing trick is the **two-phase merge**: `git merge
--no-commit` produces the merged *bytes* (so re-verify runs on exactly the bytes that ship) **without
yet creating the authoritative merge commit**, so the resume authority (the merge commit + its
trailer) does not exist until the very last step. The fixed success-path order is:

1. **state-fragment merge into `state.json`** (idempotent within the task's own namespace — §6.2
   single-writer-per-key: re-applying the same fragment is last-writer-wins against the task's *own*
   keys, never cross-task);
2. **`git commit` — the merge commit on the run branch** (the durable authority — its
   `Guardrails-Task:` trailer is the resume record, §6);
3. **journal `Succeeded` + `mergeSequence`.**

Crash windows, each resolved by the §6 pre-pass:
- **Crash before (1)** (during re-verify, or after re-verify before the fragment write): no merge
  commit exists (it is created only at step 2), the staged `--no-commit` merge is in the worktree's
  index only and is discarded by the resume reset → resume treats the task **not-merged**, re-runs
  it. No fragment was written. Clean.
- **Crash after (1), before (2):** the fragment is in `state.json` but **no merge commit exists yet**
  (this is exactly why the commit is deferred) → resume treats the task **not-merged**, re-runs it;
  on the retry the fragment re-merges idempotently (own-namespace last-writer-wins) and the
  `mergeSequence` is assigned fresh. No lost work, no cross-task corruption. *Honest residual: a
  `state.json` fragment can briefly exist for a not-yet-committed task — but it is the task's OWN
  namespace, the producer re-runs, and no downstream task ran against it (downstream worktrees are
  cut off the integration HEAD, which lacks the commit).*
- **Crash after (2), before (3):** the merge commit exists but the journal says `running` → resume
  treats the task **succeeded from the merge commit** (§6 step 2) and does NOT re-run or re-merge.
  The fragment is already in `state.json` from (1). Consistent — and this is the **only** window in
  which the merge commit is the authority, and the fragment is guaranteed present because it was
  written at (1).
- **Crash during (2)** (git creating the commit): git's commit is atomic — the run branch either has
  the merge commit or it does not; resume reads whichever state landed.

**The `--no-commit` two-phase merge + state-fragment-before-commit is the load-bearing choice** (a
plain `git merge --no-ff` that commits *before* re-verify would make the merge commit — the resume
authority — exist while re-verify is still running, so a crash there would re-green an unverified
union; and writing the fragment *after* the commit would let a crash between them re-green a task
whose state keys are missing — the exact B1 split-brain, merely relocated). **This is the genuinely
new failure mode encoding B1 surfaced, and it is closed by deferring the commit, not waved past.**
*The honest residual that remains: the rollback (`reset --hard preHead`) is one more git invocation;
its own non-zero exit must route to `needs-human` (not throw) — pinned by the Stage-2 tests. We use
`reset --hard preHead` rather than `merge --abort` precisely because `merge --abort` does NOT restore
cleanly on the W3 dirtied-tracked path (it fails rc=128 "Entry not uptodate").*

**The user's checkout is never the `<integ>` here.** `preHead` and the `reset
--hard` rollback both act on the harness-owned integration worktree's run branch — a disposable tree the
user's editor and other processes never touch. This is what dissolves most of counter-2 (the
"pristine-primary precondition"): the precondition still exists, but it is now a precondition on a
tree the harness fully owns, not on the user's live working directory.

**Why each piece:**

- **`git reset --hard preHead` is git's own, battle-tested rollback — we are not re-implementing
  it.** On every non-success path it restores the **integration worktree's** working tree and index
  to exactly `preHead` and clears `MERGE_HEAD`. We deliberately do NOT use `git merge --abort`: it is
  not universal (it fails `rc=128 "Entry not uptodate"` on the W3 dirtied-tracked-file path, leaving
  the tree stuck mid-merge), whereas `reset --hard preHead` works for conflict, re-verify-fail, and
  dirtied-tracked alike (verified empirically). `reset --hard` does not touch untracked/ignored
  files, so git-ignored build artifacts (`bin/`, `obj/`) survive it. (The harness owns the
  integration worktree as a single writer under the lock; the only files it touches there are tracked
  source — `state/` lives in the plan folder, git-ignored or outside the merge surface. **Because the
  integration worktree is harness-owned and disposable, the residual is only that re-verify guardrails
  must be read-only on tracked files; §Devil's-advocate counter 2.** See "the worktree root" + §Schema
  §1.)

- **A clean merge is NOT the moment of truth — re-verify is.** A git merge with no textual
  conflict can still ship broken bytes (the semantic/logical conflict: two tasks edit
  non-overlapping lines that are jointly inconsistent; one renames a method the other added a
  call to). The in-worktree guardrails verified the *pre-merge* bytes of *this* task in
  *isolation*; they never saw the union. **The only sound gate is: re-run this task's guardrails
  against the merged integration worktree — the bytes that actually ship.** This is precisely the
  property the brief demands ("the merge target's verification must run against the bytes that
  actually ship"), and precisely the property plan 05 could never have, because its "merge" was an
  in-place shared-workspace write with nothing to re-verify against. (And the bytes that ship to the
  user are precisely the run-branch bytes the user reviews — or that `--merge-on-success`
  fast-forwards — so "re-verified bytes" and "delivered bytes" are the same commit.)

- **The rollback for the clean-but-broken case is the same `reset --hard preHead` (thanks to
  `--no-commit`).** Because the merge was staged with `--no-commit`, a failed re-verify has **no
  merge commit to undo** — `git -C <integ> reset --hard preHead` restores the **integration
  worktree**'s index and worktree to exactly `preHead` and clears `MERGE_HEAD`. A failed re-verify
  therefore **leaves the integration worktree UNCHANGED** — and `state.json`, the journal, and the
  `mergeSequence` are untouched because the settle tail (which writes them) only runs on full success.
  The user's checkout was never touched at all. The critical section's only terminal states for a
  non-success are "rolled back (conflict)", "rolled back (re-verify failed)", and "rolled back
  (tracked-file dirtied)", all returning the run branch to `preHead` with no partial state — and a
  non-zero exit from the rollback itself is an actionable `needs-human` halt, never a throw.

- **Which guardrails re-run?** *This task's own* guardrails (the ones that just passed in the
  worktree). Re-running *all* tasks' guardrails on every merge is O(N²) and wrong-shaped; the
  task that is merging is the one whose contribution might break the union, so its guardrails are
  the relevant ones. A task whose guardrails are *purely about its own subtree* will usually pass
  trivially post-merge (its files merged cleanly); a task with a *whole-repo build/test guardrail*
  (the common gate archetype) is exactly the one that catches a semantic conflict — which is why
  the **terminal integration gate (§2a) is now a HARD harness requirement**, not just skill
  doctrine. **Honest limit named:** if a task's guardrails do not transitively exercise the broken
  interaction, re-verify can pass while the union is subtly broken — this is the residual
  false-green surface, addressed in §Devil's-advocate and bounded (not eliminated) by the
  harness-enforced terminal whole-repo gate (§2a) that re-verifies the *final* integration HEAD
  before the run may report green. (This is honest: worktrees give *isolation*, not *omniscient
  integration testing*.)

- **Re-verify must be read-only on tracked files — and the harness now CHECKS it (W3).** After a
  passing re-verify (on the staged `--no-commit` merge), the harness asserts `git -C <integ> status
  --porcelain` shows only the expected staged merge and no *other* tracked modification. If a
  re-verify guardrail dirtied a tracked file (a "fix-up" that rewrites source as a side-effect of
  "verifying"), the harness rolls back with `git -C <integ> reset --hard preHead` and halts
  `needs-human` with a clear message — an honest halt, never a silent corruption that would dirty the
  next task's merge. (`reset --hard` is required here specifically: `merge --abort` FAILS rc=128 on a
  path that is both staged and unstaged.) This makes "re-verify guardrails are read-only on tracked
  files" a *checked precondition*, not a hope (it was only a hope in the prior draft;
  §Devil's-advocate counter 2). Build artifacts a re-verify guardrail produces (`bin/`, `obj/`) must
  be git-ignored — a checked setup precondition where feasible — so they are not "tracked
  modifications" and `reset --hard preHead` leaves them (it does not touch untracked/ignored files),
  never dirtying the tree. **Catalogue doctrine for M4 (Q3):** re-verify-eligible guardrails MUST be
  read-only on tracked files — `plan-breakdown` must not place a write-fixup guardrail (formatter,
  codegen, auto-edit) in the re-verify set, and `guardrails-review` flags any re-verify guardrail that
  mutates tracked files; the W3 porcelain check is the runtime backstop that turns a doctrine
  violation into an honest halt.

- **Cost of re-verify under the lock — stated HONESTLY (W2).** Re-verify runs *inside* the
  serialize-merges critical section (it must — another merge changing the integration worktree
  mid-re-verify would invalidate it). The earlier framing ("only the merge tail serializes") was
  too kind: **re-verify is the full guardrail suite re-running per task, single-file, under one
  global lock.** For a DAG of N tasks each carrying a whole-repo build/test guardrail, that is N
  serial full-suite runs at the merge tail — the parallelism win is in the *actions* (the
  concurrent agents), not in integration verification, which is inherently serial against one
  integration worktree. Named in §Honest costs as a real throughput ceiling, not hidden.

### 2a. Terminal integration gate — a HARD harness requirement (resolves B2)

Per-task re-verify (§2) checks the bytes that ship for *that* task's merge, but it is only as
strong as that task's guardrails. A multi-leaf plan whose tasks each carry only narrow per-task
guardrails can drain **entirely green** against a run branch whose *union* does not even build —
the per-task gates each passed, no task's gate exercised the whole repo, and nothing re-verified
the final HEAD. In the prior draft "re-run THIS task's guardrails on merged bytes + a terminal
whole-repo gate" was only **skill doctrine** (`plan-breakdown` convention), so a hand-edited or
weak plan could bypass it. **The product owner has decided this is a harness invariant.**

**The harness cannot infer "this guardrail is whole-repo" by inspection** — a build/test command is
opaque. So the integration gate must be **explicitly declared**, and the harness enforces three
things:

1. **A task-level marker, `"integrationGate": true` in `task.json`** (§3 schema edit below), names a
   task as an integration gate. Its guardrails are the whole-repo gate that must hold on the final
   merged union.
2. **Validation error (new code `GR2015`, the next free STRUCTURAL number — NOT the freed
   GR2013/GR2014, which §New-diagnostic-codes assigns to the git-required gate):** a **multi-leaf**
   plan (≥2 DAG sinks, or any plan with >1 task that is not a single linear chain) with **no task
   marked `integrationGate: true` that is a DAG sink** fails `validate`. The marker must sit on a
   **sink** (a task no other task depends on) so it runs last, against the fully-merged union.
3. **The run's green verdict is contingent on every `integrationGate` task passing its guardrails
   on the FINAL integration HEAD.** After the scheduler drains, before the run may report green, the
   harness re-verifies each integration-gate task's guardrails against the final integration
   worktree HEAD (the whole union). A failure there halts the gate task `needs-human` and the run is
   **not** green. **`--merge-on-success` refuses to deliver** a run that has no passing integration
   gate — it never fast-forwards a union the terminal gate did not certify.

**Single-task / single-leaf plans are EXEMPT.** A plan that is one task, or a single linear chain
with exactly one sink, has no *union* to break — the last task's own re-verify already ran against
the only HEAD there is. The validation error fires only when there is a union the per-task gates
could leave uncertified (≥2 leaves merging into a shared sink, or ≥2 sinks). This keeps the
common small plan friction-free while closing the multi-leaf drain-green hole.

**Note the per-task re-verify and the terminal gate are the SAME mechanism at two scopes:** §2's
per-merge re-verify runs a task's guardrails on *its* merge; §2a's terminal gate runs the
gate-task's guardrails on the *final* HEAD. The gate task is just the sink whose guardrails are
declared whole-repo and whose pass is made a run-green precondition. No new guardrail machinery —
the same `GuardrailRunner` invocation against the integration worktree, gated on the marker.

M4 skill work: `plan-breakdown` MUST always emit such a sink for a multi-leaf plan (a terminal
`integrationGate: true` task carrying the whole-repo build/test suite); `guardrails-review` flags
its absence as a BLOCKER.

### Re-examining the 4 original worktree blockers (plan 05 §1)

Plan 05 rejected worktrees on four load-bearing soundness problems. Each is resolved here:

| # | Original blocker | Resolution in this design |
|---|---|---|
| (a) | **Post-merge atomicity** — a failed re-verify leaves the shared workspace half-merged | **Resolved by §2:** the merge is into the *harness-owned integration worktree* on run branch `guardrails/run-<runId>`, not a shared in-place write and not the user's checkout, and it is a **two-phase `--no-commit` merge** so re-verify runs before any commit exists. `preHead` is recorded; **every** non-success path (conflict, clean-but-broken re-verify, OR a re-verify that dirtied a tracked file) is a single `reset --hard preHead` (which also clears `MERGE_HEAD`; `merge --abort` is NOT used — it fails rc=128 on the dirtied-tracked path), returning the integration worktree to exactly pre-merge bytes under the serialize lock — and `state.json`/journal/`mergeSequence` are written only on full success (B1), so they roll back with it. The user's checkout is never touched. There is no "half-merged" terminal state and no state/git split-brain. |
| (b) | **Non-idempotent re-verify / CRLF re-normalization on a fresh checkout** — a fresh worktree re-applies `autocrlf`, so the same source hashes differently and a hash-based guardrail flaps | **Resolved by byte-identity discipline:** (i) the design adds a committed **`.gitattributes`** requirement to the workspace (`* -text` or explicit `text eol=lf` policy) so checkout never re-normalizes line endings; (ii) the harness uses `git worktree add` (which checks out per the repo's *own* attributes — identical to the integration worktree, not a re-normalized copy) rather than a file-copy; (iii) re-verify runs on the *merged integration worktree's* actual on-disk bytes (the bytes that ship), so there is no "fresh checkout vs original" hash comparison at all — the `captureHashes`/`tests-untouched` raw-byte-hash dance is no longer the protection mechanism (physical isolation is). Concretely: `plan-breakdown` stops emitting the hash-based test-protection triad; a test file an implementation task must not edit is protected because the implementation task's worktree edits never merge a test change unless the task legitimately owns it, and if it does collide the merge conflicts → human. **This removes the CRLF hazard at its root rather than fighting it.** |
| (c) | **Unsound AI auto-resolve** — the merging task's guardrails don't cover the clobbered sibling's hunk | **MOOT:** conflicts → human in v1. No AI resolver. The "guardrails don't cover the sibling's hunk" problem cannot arise because nothing auto-resolves; a conflict is a hard halt. (The resolver is v2-bet-#1's #57 half, where re-running the *incoming* task's guardrails gates the resolution — explicitly out of v1.) |
| (d) | **Tautological cross-task state (#63)** — a downstream task reading an upstream's `actionExitCode` from state is always 0 | **MOOT:** downstream tasks read upstream's **merged workspace outputs** (real produced files, now present in the integration worktree that the downstream worktree is created off) via the normal `GUARDRAILS_STATE_IN` snapshot + the produced files on disk — never a synthesized exit-code channel. Because a downstream worktree is created off the *current* integration worktree HEAD (the run branch, which includes the upstream's merged commit), the downstream physically sees the upstream's real outputs. The `actionExitCode`-in-state channel is not built (same decision as plan 05 §2.4 — it was always tautological). |

### Seams and contracts touched

**Three seam decisions are specified up front because each crosses a seam that does not exist on
master today** (verified against `feat/worktree-per-task`: `ITaskExecutor.ExecuteAsync(TaskNode,
ct)` takes no worktree handle; `GuardrailRunner` is `internal sealed`, constructed once inside
`TaskExecutor`'s ctor from executor-private collaborators; `RunReset` is a dependency-free static
`System.IO` utility; `WorkspaceLock` is a real FIFO shared/exclusive lock held around the whole
`ExecuteAsync`). Spelling them out now keeps M1/M2/M3 from discovering them mid-implementation.

- **`IWorktreeProvider` / `WorktreeManager` (new, `Execution/`)** — the only place `git` is
  invoked. Fake for M1; `GitWorktreeProvider` for M2+. Owns integration-worktree creation,
  per-task create/discard/merge/prune, the optional end-of-run merge, and every git flag.

- **SEAM 1 — Executor↔scheduler worktree handle (M1, a signature change).** `ExecuteAsync` has no
  way to receive a worktree path or return a handle for merge-back today. **The scheduler creates
  the per-task worktree (off the integration HEAD) and passes a `WorktreeHandle` INTO `ExecuteAsync`;
  the executor returns it (on `TaskResult`) for merge-back.** New signature:
  `Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken ct)` —
  a ripple through `ITaskExecutor` and **every test fake** (`FakeExecutor` in `SchedulerTests`,
  `RecordingExecutor` in `CostCapSchedulerTests`, plus any others). `TaskExecutor.ResolveWorkingDirectory`
  returns `worktree.Path` instead of `_plan.Workspace`. This reshapes M1's `IWorktreeProvider` seam:
  M1 ships the `WorktreeHandle` type + the signature change + a `FakeWorktreeProvider`, with merge
  a no-op, proving the scheduler de-serializes with NO git.

- **SEAM 2 — Merge-and-settle (M3, the B1 + re-verify contract).** `GuardrailRunner` is `internal
  sealed` and constructed privately inside `TaskExecutor` from executor-private collaborators, bound
  to the per-attempt env/log/snapshot contract — so re-verify is **NOT a free "second invocation"**
  from the scheduler. Combined with B1 (the state-merge + `mergeSequence` + `Succeeded` journal
  write must move under the lock, after re-verify), the cleanest contract is: **the EXECUTOR exposes
  a `MergeAndSettleAsync` operation** that does the whole §2 tail — commit the worktree → (caller
  holds the lock) → `git merge --no-commit` into the integration worktree → re-verify via the
  executor's OWN `GuardrailRunner` against the integration worktree → on pass: state-fragment merge →
  `git commit` (the deferred merge commit) → consume `mergeSequence` + journal `Succeeded` (in that
  fixed order, §2 crash-window rule) → on fail/conflict/dirty: `reset --hard preHead` + journal
  `needs-human`, writing no fragment and consuming no sequence. **The SCHEDULER holds the serialize-merges lock
  around the `MergeAndSettleAsync` call** (the lock is a scheduler concern; the settle logic is an
  executor concern that already owns the `GuardrailRunner`, the `StateManager`, the `AttemptJournaler`,
  and the `mergeSequence`). This keeps the executor/scheduler split coherent: the scheduler owns
  *concurrency* (which worktrees, which lock), the executor owns *a task's journal/state transitions*
  (unchanged ownership boundary — the B1 move keeps those transitions inside the executor, just
  relocated from the in-worktree success path to `MergeAndSettleAsync`, which the scheduler now gates
  with the lock). Re-scoped M3 is therefore a `TaskExecutor`/`AttemptJournaler` refactor — moving
  `CompleteSucceededOrInvalidFragment`'s fragment-merge + `ReserveMergeSequence` + `RecordAttempt(Succeeded)`
  out of `RunAttemptAsync` and into `MergeAndSettleAsync` — **not** "a re-verify invocation site."

- **SEAM 3 — `RunReset` gains a git dependency (M3).** `RunReset` is a dependency-free static
  `System.IO` utility (`Fresh`, `Task`). Worktree pruning + run-branch deletion forces git into it.
  **Decision: `RunReset` stops being a static utility — it takes an `IWorktreeProvider` (the git ops
  live behind that seam, not inline in `RunReset`)**, so `Fresh` additionally calls
  `IWorktreeProvider.PruneOrphans` + deletes the run branch, and `Task(taskId)` discards that task's
  worktree. Keep it unit-testable by injecting a `FakeWorktreeProvider` in tests (the same fake M1
  introduces); the `System.IO` deletions stay direct. (Alternative considered: keep `RunReset`
  static and move git into a separate collaborator the CLI calls alongside it — rejected as two
  call-sites for one logical "reset," easy to leave one un-wired.)

- **`Scheduler.cs`** — **delete `WorkspaceLock` outright** (W2 — it is NOT "degenerated to
  exclusive-only"; the binary shared/exclusive lock has no role once each action has its own tree).
  Replace it with: (i) **one integration worktree created at run start** (before any task), (ii)
  per-task worktree creation off the integration HEAD before `ExecuteAsync` (SEAM 1), (iii) a
  **serialize-merges lock = a plain `new SemaphoreSlim(1, 1)`** held around the executor's
  `MergeAndSettleAsync` call (SEAM 2) *into the integration worktree*, (iv) the **terminal
  integration-gate re-verify** (§2a) against the final HEAD before reporting green, and (v) an
  **end-of-run hook**: if the whole run went green AND `mergeOnSuccess` is set, merge the run branch
  into the user's original branch (else leave the run branch for review). The `exclusive`/
  `task.Exclusive` admission gate is **removed**: actions no longer contend for the workspace because
  each has its own. `maxParallelism` still caps worker count (default **2** in worktree mode — §2
  SSOT; **lowered from master's default of 4**, a real behavior change with test impact — see
  Milestones M2).
- **`TaskExecutor.cs`** — `ResolveWorkingDirectory` returns the handed-in `worktree.Path` (SEAM 1)
  instead of `_plan.Workspace`. The retry loop's "discard + recreate worktree" replaces
  `RestoreAncestorCaptures` (deleted along with the triad). New `MergeAndSettleAsync` (SEAM 2)
  reuses the executor's existing `GuardrailRunner` against the **integration worktree** — **no new
  guardrail machinery**, plus the relocated state-merge/journal tail (B1).
- **`ProcessRunner.cs`** — **unchanged.** It already takes `workingDirectory` per call; the
  worktree path flows through that parameter. (This is a deliberate design goal: the isolation is
  achieved by *where* we point the existing runner, not by changing how it runs.)
- **`Loading/PlanValidator.cs` + `Loading/DiagnosticCodes.cs`** — new git-required validation
  (workspace is a git repo top-level) + the **terminal integration-gate** validation (§2a) + new GR
  codes (below). The freed triad codes GR2013/GR2014 are **reused** for the git-required gate (the
  triad never shipped externally) — their test *messages* flip from capture-escape/restore-without-
  capture to not-a-git-repo/MAX_PATH (a message rewrite, noted in M2). The terminal-gate code
  `GR2015` is a **separate fresh number**. Remove the triad validators (`ValidateCaptureHashPaths`,
  `ValidateRestoreOnRetry`).
- **`Model/TaskNode.cs`** — remove `Exclusive`, `CaptureHashes`, `RestoreOnRetry`; **add
  `IntegrationGate` (`bool`, default false; §2a)**. (No `writeScope` to remove — it never landed on
  master; the product owner has confirmed the drop.)
- **`Model/RunConfig.cs`** — optional `worktreeRoot` override (default: a sibling temp root, see
  below); optional `mergeOnSuccess` (default **false**); `maxParallelism` default lowered to **2**
  for worktree mode (from master's **4**). (`--merge-on-success` CLI flag in `Guardrails.Cli`
  overrides the config flag.)
- **`State/RunReset.cs`** — now takes `IWorktreeProvider` (SEAM 3): `--fresh`/`reset` wipe the
  worktree root (per-task trees **and** the integration worktree) + prune orphan worktrees
  (replacing the `captured/` wipe, which goes away with the triad). `--fresh` also deletes the run
  branch `guardrails/run-<runId>` and any per-attempt branch `guardrails/<runId>/*` of an abandoned
  run (harness-owned runtime state).
- **Deleted outright:** `Execution/WorkspaceLock.cs`, `Execution/CapturedFileStore.cs`,
  `Execution/FileHashCapture.cs`, `TaskExecutor.RestoreAncestorCaptures`, the triad properties on
  `TaskNode`, the two triad validators — see §Milestone re-scope for which milestone compiles
  without each, and the ~175-assertion / 12-file test rewrite they force.

### The worktree root (off a sibling, OUTSIDE the workspace) — TWO kinds of harness worktree

There are now **two kinds of harness-owned worktree**, both under the same temp root, both
harness-owned runtime state, both wiped by `--fresh` and pruned on resume:

1. **The integration worktree** — exactly **one per run**, at `<root>/_integration`, on run branch
   `guardrails/run-<runId>`, created off the user's HEAD at run start. The sole merge target.
2. **The per-task worktrees** — one per task **attempt**, at `<root>/<taskId>`, on a per-attempt
   branch `guardrails/<runId>/<taskId>/<attempt>` (B3 — distinct per retry so branch names never
   collide and `--fresh`'s `git branch -D guardrails/<runId>/*` deletes them all cleanly), created
   off the *integration worktree's* HEAD.

`git worktree add` is given a path **outside** the workspace tree — a sibling root; a default
under `<plan-dir>/state/worktrees/...` is **tempting but WRONG** (it would nest worktrees under the
plan folder, compounding Windows MAX_PATH; worse, if `state/` is inside the workspace repo it
pollutes `git status`). The decision:

- **Default root: a sibling temp directory keyed by run**, e.g.
  `%TEMP%/guardrails-worktrees/<workspace-hash>/<runId>/` (POSIX: `$TMPDIR/...`), holding both
  `_integration/` and `<taskId>/` subtrees. Outside the workspace entirely → never pollutes the
  user's checkout `git status`, never nests under MAX_PATH-deep plan paths.
- **Overridable** via `guardrails.json: worktreeRoot` for users who want them on a specific
  fast/large volume.
- Both kinds are **harness-owned runtime state**, wiped by `--fresh` and pruned on resume — never
  committed, never part of the baseline. The run branch `guardrails/run-<runId>` is the one durable
  artifact left for the user to review (it survives `--fresh` only as a ref the user may have
  already merged; an abandoned run's branch is deleted by `--fresh`).

This mirrors the user's own memory ("worktrees under TEMP, not C:\Dev"): throwaway trees go under
a clarifying TEMP root.

### Scheduler integration (replacing `exclusive`/`WorkspaceLock`)

- **Integration worktree at run start:** before any task is dequeued, the scheduler asks
  `IWorktreeProvider.CreateIntegration(runId)` — one run branch `guardrails/run-<runId>` off the
  user's HEAD, captured into an `IntegrationHandle` that flows to every merge-back. The user's
  checkout is read-only from here until (optionally) the end-of-run merge.
- **Worktree per task:** the worker, on dequeuing a ready task, asks `IWorktreeProvider.Create`
  (off the **integration worktree's** current HEAD) *before* calling `_executor.ExecuteAsync`, and
  hands the worktree path down. (M1: a fake provider that just returns a temp dir, proving the
  scheduler no longer serializes.)
- **The Channel/worker model is unchanged:** Kahn readiness → `Channel<TaskNode>` → N workers.
  What changes is what happens *around* `ExecuteAsync`: create-worktree before, merge-back (into
  the integration worktree) after.
- **Serialize-merges lock:** a plain `new SemaphoreSlim(1, 1)` the scheduler holds around the
  executor's `MergeAndSettleAsync` (the whole §2 tail) *into the integration worktree*. Actions run
  fully concurrent; the merge tail serializes — but be honest about what serializes (W2): the tail
  is the **full guardrail suite re-running per task, single-file under one global lock**, plus the
  state-merge/journal commit. `WorkspaceLock` is **deleted outright**, not "degenerated to
  exclusive-only" — the binary shared/exclusive lock has no role once each action has its own tree,
  and no action is ever `exclusive` because no two actions share a tree.
- **`maxParallelism`** caps workers as today; it now caps *concurrent worktrees/actions*, which
  is the real resource (disk + agent processes). **Default 2 in worktree mode** (§2 SSOT) so the
  out-of-box run is not N multi-GB trees. The merge lock is independent and always 1.
- **End-of-run merge (opt-in):** when the scheduler drains and the run is wholly green, if
  `mergeOnSuccess` (config) or `--merge-on-success` (CLI) is set, it calls
  `MergeRunBranchIntoUserBranch` (below). Default OFF → the run branch is simply left for review.
- **Git required:** `validate` (and a run pre-flight) asserts the workspace is a git repo
  top-level (`git rev-parse --show-toplevel` equals the workspace). Non-git → error, no silent
  shared-workspace fallback.

### `--merge-on-success` (opt-in end-of-run delivery; default OFF)

By default a run **never writes the user's branch**: it leaves `guardrails/run-<runId>` for the
user to inspect (`git log`, `git diff <user-branch>...guardrails/run-<runId>`) and merge by hand.
That is the honest-halt default — the user reviews the *checks once* and then the *merge once*.

`--merge-on-success` (or `guardrails.json: "mergeOnSuccess": true`) opts into automatic delivery:
**only if the whole run went green** (no `needs-human`/`failed`/`blocked` task), at run end the
harness attempts to bring the run branch into the user's **original** branch (captured in the
`IntegrationHandle` at run start):

- **Fast-forward when possible** (the user's branch has not advanced since run start — i.e. its
  current tip still equals the captured original HEAD): `git -C <user-checkout> merge --ff-only
  guardrails/run-<runId>`. This is the clean, common case and writes the user's branch with zero
  merge commit.
- **If the user's branch advanced during the run** (they committed to it mid-run — see
  §Devil's-advocate counter 6), a fast-forward is impossible. The harness does **not** force
  anything: it attempts a normal `git merge`, and **a conflict (or a non-green re-verify of the
  merged result, since the user's new commits were never integration-tested) halts to
  `needs-human`** with the run branch left intact and a message telling the user to merge manually.
  `--merge-on-success` is best-effort delivery, never a destructive overwrite.
- The user's working tree must be clean for the FF/merge to apply; if it is dirty, the harness
  **skips** the auto-merge and falls back to "left for review" with a clear message — it never
  stashes or discards the user's uncommitted work.

### 5. `writeScope`'s fate — DROP it (confirmed by the product owner)

`writeScope` was the disjoint-scope ownership field (plan 05 §4): a per-task declared glob set
the harness enforced by post-hoc revert. **It never shipped on master** (it lives only on the
abandoned branch), so "dropping" it is "do not adopt it."

**Decision (product-owner-confirmed): drop entirely. Not even a lightweight optional form in v1.**

Rationale (KISS / YAGNI):
- **Physical isolation makes per-task write-enforcement unnecessary.** The entire purpose of
  `writeScope` was to make concurrent shared-workspace writes safe. With one worktree per task,
  there is no shared workspace and no write to enforce against. The mechanism it justified is
  gone.
- **It re-imports the exact complexity the product owner rejected.** `writeScope` requires the
  glob matcher (`Overlaps`/`IsInScope`), the enforcer, the baseline byte store, and the GR2015/2016
  family — the "self-implemented likely-to-have-bugs solution." Keeping it "as a hint" keeps the
  matcher and its 27-row truth-table-of-doom (plan 06 §3) without the enforcement that was its
  only justification.
- **The merge already IS the contract.** "What a task is expected to contribute" is expressed by
  *what its branch changes* — git's diff is the merge contract, reviewable directly, with no new
  field. A reviewer asking "did this task touch only what it should?" reads `git diff
  guardrails/run-<runId>...guardrails/<runId>/<taskId>` — the real, computed answer, not a declared
  glob the enforcer might mis-match.

**The lightweight-optional alternative (considered, rejected):** keep `writeScope` as a
*non-enforced* review hint that `guardrails-review` reads to flag "this task's diff touched files
outside its declared intent." This has *some* value (a reviewer signal) but: (i) it is pure
documentation a code comment or the task description already carries; (ii) an unenforced field
rots (nothing fails when it is wrong); (iii) it tempts a future "let's just enforce it" that
re-opens the whole plan-05 can. **If a contribution-declaration is ever wanted, the right v1 form
is a free-text `task.description` convention, not a structured glob.** The product owner has
confirmed the drop — this is no longer an open decision.

### 6. Resume / crash — reconciliation against the run branch (RESOLVED, testable)

The integration-worktree model gives resume a **clean durable record it did not have before**: the
run branch `guardrails/run-<runId>`'s **first-parent merge commits ARE the record of which tasks
merged**. The journal is an optimization; the run branch is the truth. The reconciliation rule
below is concrete and idempotent — it is no longer an open decision.

**The merge-commit detection — trailer only (B3).** Every merge-back is `git merge --no-ff
guardrails/<runId>/<taskId>/<attempt>` into the run branch, which produces a merge commit whose
message the harness writes in a fixed, parseable form:

```
guardrails: merge task <taskId> (run <runId>)
Guardrails-Task: <taskId>
Guardrails-Run: <runId>
```

Detection walks the run branch's **first-parent** history (`git -C <integ> log --first-parent
--format=...` over `guardrails/run-<runId>`) and reads the `Guardrails-Task:` / `Guardrails-Run:`
trailers. The set of `<taskId>` whose trailer matches the resuming `<runId>` on a first-parent
merge commit is the **merged set**.

**The prior "second-parent ref cross-check" is DROPPED (B3).** It was illusory: a merge commit
stores parent **SHAs**, not ref names, so there is no `guardrails/<runId>/<taskId>` *ref* to read
off the second parent — and even the per-attempt branches are deletable by `--fresh`, so a ref
that existed at merge time may be gone at resume time. The only sound, harness-owned record is the
**trailer the harness itself wrote** on a first-parent merge commit of a branch *it* owns and
appends to. The integrity boundary is exactly the one already documented elsewhere in this design:
**the user does not rewrite the run branch.** Within that boundary the trailer is authoritative;
outside it (a hand-edited run branch) the user is outside the contract — documented, not silently
handled. Per-attempt branch names (`.../<attempt>`) also mean retried attempts never collide on a
branch name, so the resume walk, the prune, and `--fresh` deletion are all coherent.

**The resume pre-pass rule (idempotent, double-merge impossible-by-construction):**

1. Build the *merged set* by walking the run branch's first-parent merge commits' trailers (above).
2. **A task in the merged set is treated `succeeded`** — *regardless of what the journal last
   wrote*. The merge commit beats a lost journal write. (This closes counter-5's crash window: a
   crash after the merge commit but before the journal `succeeded` write resolves to succeeded.)
   **B1 corollary:** because the §2 success path merges the task's `state.json` fragment **before**
   the git merge commit, any task the merge commit certifies already has its fragment durably in
   `state.json` — a merged-set task can never be re-greened *without* its state. The reverse window
   (fragment written, no merge commit yet) lands the task **outside** the merged set, so it is
   re-run (step 3/4) and the fragment re-merges idempotently in its own namespace. Either way no
   downstream task ran against an uncertified fragment (downstream worktrees are cut off the
   integration HEAD, which lacks the un-committed merge).
3. **A task NOT in the merged set, journaled `running`** (crashed mid-attempt) **is reset to
   `pending`** and re-run — its orphan per-attempt worktree(s) pruned first (below).
4. A task not in the merged set with any other journal status follows existing M3/M4 resume
   semantics (`needs-human`/`failed`/`blocked` → fresh budget; `pending` → run).
5. **Re-merging is impossible by construction:** a merged task is treated `succeeded` at step 2 →
   it is never re-attempted → no new per-attempt branch is ever created or re-merged for it. There
   is therefore **no double-merge path**, so the merge step needs no separate "already merged?"
   guard beyond this pre-pass. (This rests on the pre-pass alone — sound — and no longer on the
   dropped second-parent cross-check.) (If a run branch somehow already contains a task's merge AND
   the task is somehow re-queued — a bug — the merge would be an empty no-op `git merge` of an
   ancestor, which git reports as "already up to date", not a conflict; but the pre-pass means we
   never reach that.)

**Orphaned worktrees on resume.** A crashed run leaves worktrees registered in `.git/worktrees/`.
The pre-pass calls `IWorktreeProvider.PruneOrphans(liveTaskIds, integration)`: `git worktree prune`
(clears trees whose directories vanished) then `git worktree remove --force` for any
`guardrails/<runId>/<taskId>/<attempt>` per-task tree not for a task about to re-run. The
**integration worktree itself is never pruned** on resume — it is reattached (the run branch and its
merge commits are the durable record we resume *onto*). Idempotent: pruning an already-pruned tree
is a no-op.

**A partially-merged integration worktree on crash.** Because the merge is staged with
`--no-commit` and the **merge commit is created only at the end of the settle tail**, a crash
*during* the merge/re-verify window leaves a staged-but-uncommitted merge in the integration
worktree's index — and **no merge commit on the run branch**. The resume pre-pass reads the
**merge-commit trailers** (step 1) as the authority: with no commit, the task is **not** in the
merged set, so it is re-run, and the stale staged merge (and any `MERGE_HEAD`) is cleared by an
explicit `git -C <integ> reset --hard <runBranchHead>` in the resume pre-pass before the re-run. The only way a task is in the merged set is if `git commit`
completed — and git's commit is atomic, so the run branch either has the commit or it does not.
There is no "half-merged-and-counted" state. The user's checkout is irrelevant here: it was never
written, so a crash can never have half-written it.

**`--fresh` / `reset`.** `RunReset` (now holding an `IWorktreeProvider`, SEAM 3) `Fresh` additionally
wipes the entire worktree root (per-task trees **and** the integration worktree), runs `git worktree
prune`, and deletes both the abandoned run branch `guardrails/run-<runId>` **and every per-attempt
branch** (`git branch -D guardrails/<runId>/*` — coherent precisely because attempt branches are
distinct, B3). `RunReset.Task(taskId)` discards that task's worktree(s) via the provider (so its
re-run starts pristine) instead of clearing a `captured/` baseline. The user's checkout, the user's
branch, and `seed.json` are untouched.

---

## Honest costs / limits of THIS approach

The last design's disqualifying sin was *overselling*. So, plainly — what this costs and where it
is the wrong tool:

1. **Disk amplification.** N concurrent tasks + the one integration worktree = **(N + 1) working
   trees** on disk. `git worktree` shares `.git/objects` (the expensive part — history/blobs), so
   the amplification is **(N + 1) × (checked-out working-tree size)**, not N × repo size. The
   integration worktree is the "+1" the previous (merge-into-user's-checkout) model did not have —
   a deliberate, named cost of never writing the user's tree. To keep the out-of-box footprint
   sane, **worktree mode defaults `maxParallelism` to 2** (so 3 trees by default, not 9), and the
   lever to go higher is `maxParallelism` on a fast/large `worktreeRoot` volume. For a large repo
   (a multi-GB checkout) at a raised `maxParallelism: 8`, that is 9× the *working set* on disk —
   genuinely significant. **v2 mitigation, named not built:** worktree pooling, sparse checkout, or
   a copy-on-write filesystem (ReFS/APFS/btrfs reflinks) would make this near-free; out of v1 scope.

2. **Windows MAX_PATH.** A worktree rooted at a deep path + a deep source tree inside it can
   exceed 260 chars. Mitigations: (i) the worktree root is a *short* sibling temp path
   (`%TEMP%/guardrails-worktrees/<hash>/...`), not nested under the (possibly deep) plan folder;
   (ii) document the long-paths registry/`git config core.longpaths true` requirement;
   (iii) a run pre-flight *can* warn if `<worktreeRoot>/<longestTaskId>/<deepest-repo-path>`
   would exceed the limit. **Honest residual:** on an un-configured Windows box with a deep repo,
   this can still fail — surfaced as an honest error, not a silent corruption.

3. **Merge-back serialization throughput — stated without softening (W2).** Merge + post-merge
   re-verify + the state/journal commit are serialized (one at a time, into the single integration
   worktree, under a `SemaphoreSlim(1,1)`). Be exact about what serializes: **re-verify is the
   merging task's FULL guardrail suite re-running, single-file, under one global lock** — not a
   cheap "merge tail." If every task carries an expensive whole-repo test guardrail and they all
   finish at once, the harness runs that suite N times in series at the tail. The parallelism win is
   in the *actions* (the concurrent agents), not the merge tail — worktrees parallelize *generation*,
   not *integration verification*, which is inherently serial against one integration worktree, and
   the **terminal integration gate (§2a) adds one more full-suite run** on the final HEAD before
   green. **This is a real throughput ceiling**, not hand-waved. (The earlier "only the merge tail
   serializes" framing understated this; corrected here.)

4. **Merge-conflict → human friction.** Two tasks legitimately editing the same file conflict →
   `needs-human`. On a plan with poorly-separated tasks this could halt frequently. The mitigation
   is plan structure (`plan-breakdown` favoring file-disjoint tasks) and the DAG (a
   read-after-write edge serializes the dependent after the producer's merge, avoiding the
   conflict). But a plan with genuinely overlapping concurrent edits **will** halt — that is the
   honest cost of "no AI auto-resolve in v1," and it is the correct cost (a silent auto-merge of
   conflicting intent is worse).

5. **Where worktrees are simply WRONG.**
   - **Non-git workspace** → git-required validation error. Worktrees cannot apply; the tool
     refuses rather than degrading. (A user with a non-git directory has no parallel-execution
     story in v1 — an honest, named limitation.)
   - **A workspace where `state/` or generated artifacts are *inside* and tracked by the repo** —
     the merge surface must exclude harness/run state. Handled by `state/` living in the plan
     folder (git-ignored or outside the merge), but a misconfigured repo that tracks build output
     could see those files churn through merges into the integration worktree's run branch. **Build
     artifacts produced by re-verify on the integration worktree MUST be git-ignored** (or `reset
     --hard`, which is tracked-only, would not clean them and they would dirty the next merge). This
     is treated as a **checked setup precondition where feasible** (W3), not just documented — and
     the post-re-verify `git status --porcelain` assertion (§2, W3) is the runtime backstop that
     turns a tracked-file dirtying into an honest `needs-human` halt rather than silent corruption.
     See §Devil's-advocate counter 2, whose "ignored artifacts" residual now applies to the
     integration worktree, not the user's checkout.)
   - **A flailing agent escaping its worktree** (writing `../../primary/x` by absolute path).
     Physical isolation protects the *tree*, not the *filesystem* — an agent that writes outside
     its worktree by absolute path is unconstrained. This is **the same residual risk every
     sandbox has** and is NOT worse than the shared-workspace model (which had the identical
     hole); worktrees simply do not *add* protection here. Honest: isolation is per-working-tree,
     not per-filesystem. (`allowedTools`/sandbox is the real boundary for that, orthogonal to this
     design.)

6. **Git requirement is a real new precondition** (invariant 6 tension): "plain files, light
   setup" now includes "git installed and the workspace is a git repo." Reasonable for the
   audience, but not nothing.

7. **NEW — run-branch staleness if the user commits to their branch mid-run.** The integration
   worktree's run branch is forked off the user's HEAD at run start and never re-based during the
   run. If the user commits to their *original* branch while the run is in flight, the run branch
   becomes a fork point behind the user's branch — the run integrated against a now-stale base. Two
   honest consequences: (i) a plain `--merge-on-success` fast-forward becomes impossible (handled:
   it degrades to a real merge, and a conflict or a failed re-verify of the merged result halts to
   `needs-human` — never a force-overwrite; §Design "merge-on-success"); (ii) even a *clean* merge
   of the user's new commits with the run branch was **never integration-tested** (the user's
   commits never went through any per-task re-verify), so `--merge-on-success` re-runs a final
   whole-repo gate after the merge and halts if it fails. The default (OFF → leave for review)
   sidesteps both by handing the user a branch to merge themselves. **This is a genuinely new
   failure mode the user's-checkout-as-target model did not have** (that model wrote the user's
   live tree and so was always "current," at the cost of racing the user's editor). The trade is
   deliberate: we accept "the run branch can go stale" in exchange for "the user's tree is never
   raced or half-written." Named, not buried.

8. **NEW — the user can ignore a green run branch.** Because the default leaves
   `guardrails/run-<runId>` unmerged, a user who never reviews it gets *no delivered change* from a
   wholly-green run — an accumulation of abandoned run branches is possible. This is the honest
   cost of "honest-halt by default": the tool will not silently advance the user's branch. The
   mitigations are the end-of-run message (print the exact `git merge guardrails/run-<runId>` /
   `git log` commands and the `--merge-on-success` opt-in) and `--fresh` deleting abandoned run
   branches. A user who wants hands-off delivery sets `--merge-on-success`.

9. **NEW — a retry pays a FULL checkout cost (N2).** A failed attempt's worktree is discarded and a
   fresh one is `git worktree add`-ed off the current integration HEAD for the retry (§Lifecycle 3).
   That re-checkout is the **full working-tree cost** every attempt — orthogonal to, and on top of,
   the serialized re-verify tail (cost 3). On a large repo a task that takes several retries pays
   that checkout each time. `git worktree` sharing `.git/objects` keeps it to a working-tree
   checkout (not a clone), but on a multi-GB tree it is **significant per retry** and compounds with
   the disk amplification (cost 1). Named, not buried; the v2 mitigations (pooling / sparse / CoW,
   cost 1) would shrink it too.

---

## Process / staging (the plan-06 §9 discipline, carried forward)

**You cannot trust a parallelism mechanism proven only by a wall-clock test.** A run that
*happens* to go green at `maxParallelism: 1` proves nothing about concurrent merge-back. So,
TEST-FIRST against the real executor seam, verify independently, dogfood as a **capstone**:

- **Stage 1 — Contract first (this doc → SSOT).** Apply the §Schema-changes to
  `02-schemas-and-contracts.md` in the same change the harness work begins. Owner:
  `guardrails-architect` proposes; lead applies.
- **Stage 2 — Hand-implement, agent-team, test-first.** `guardrails-test-author` writes the
  real-executor integration suite *first* (proving each test FAILS against the current
  shared-workspace harness), then `guardrails-harness-developer` implements to green. The
  load-bearing tests (not a wall-clock test):
  - **clean-merge-but-broken-build (the false-green + B1 atomic-unit gate):** two tasks edit
    non-overlapping lines of the same file such that the `--no-commit` merge applies cleanly but the
    merged file fails to build; assert the second merge's **post-merge re-verify FAILS**, the staged
    merge is **`reset --hard preHead`-ed back to pre-merge bytes** (assert the **integration worktree's**
    run-branch HEAD sha == recorded preHead **and no new merge commit was created**), and the task
    halts needs-human. **W4 — also pin B1 (the state/git/journal
    atomic unit) on this exact test:** assert the task is journaled **`needs-human`, NOT `Succeeded`**;
    `state.json` does **NOT** contain the task's fragment keys; the **`mergeSequence` was not
    consumed** (the next sequence number is unchanged from before the failed merge); integration HEAD
    == preHead; AND the user's original branch/HEAD is untouched. *This is the single test that proves
    the four effects roll back together — it cannot pass on a design that journals `Succeeded` or
    writes the fragment before re-verify.* It is the direct analogue of plan 06's #88 test — the one
    the false-green design never had.
  - **user-checkout-untouched (the integration-worktree gate):** run any plan (including one that
    halts) with `--merge-on-success` **OFF**; assert the user's **original branch ref and HEAD sha
    are byte-for-byte unchanged** after the run, and the user's working tree is clean (no harness
    write ever reached it). *This is the test that proves the headline decision — the user's
    checkout is read-only for the whole run.*
  - **textual-conflict → abort → needs-human:** two tasks edit the same line; assert
    `reset --hard preHead`, integration worktree unchanged (run-branch HEAD == preHead), task needs-human,
    worktree preserved, dependents blocked, an independent third branch still completes.
  - **concurrent-isolation (tightened, N1):** two long-running actions in two worktrees write the
    same relative path concurrently; assert neither sees the other's bytes mid-flight. **N1 — assert
    the placement, not just the non-interference, so the test can't pass against two unrelated temp
    dirs that never merge:** each task's bytes land in the **scheduler-assigned worktree path** (the
    `WorktreeHandle.Path` the scheduler created off the integration HEAD), are **ABSENT from the
    sibling's tree**, and appear at the **integration HEAD only AFTER that task's merge-back** (not
    before). This binds the isolation claim to the real worktree topology (physical isolation + the
    merge-back ordering), gated on both being live.
  - **downstream-sees-merged-upstream:** a dependent task's worktree, created off the integration
    worktree's HEAD after the producer merged, physically contains the producer's merged output
    file (resolves blocker (d) as a test).
  - **resume-after-merge-commit-before-journal (the counter-5 gate, B3 trailer-only):** kill the run
    *after* a task's merge commit lands on the run branch but *before* the journal `succeeded` write;
    on resume, assert the task is reconciled `succeeded` **purely from the run-branch merge commit's
    `Guardrails-Task:`/`Guardrails-Run:` trailer** (§6 step 1 — NOT from any second-parent ref, which
    is dropped; the test must pass even after the per-attempt branch ref is deleted), is **not**
    re-attempted, and is **not** re-merged (no second merge commit for that `<taskId>` on the run
    branch — idempotence). Companion assertion (B1 ordering): because the fragment merged before the
    commit, `state.json` already contains the task's fragment on resume. *This is the test the
    previous "open decision" had no answer for.*
  - **resume-after-fragment-before-merge-commit (the B1 reverse-window gate):** kill the run *after*
    the §2 state-fragment merge but *before* the git merge commit; on resume, assert the task is
    **NOT** in the merged set (no trailer), is re-run, and the fragment re-merges **idempotently**
    in its own namespace with a fresh `mergeSequence` — no cross-task corruption, no double-merge.
    *This pins the load-bearing ordering choice (fragment-before-commit) the B1 fix rests on.*
  - **reverify-dirties-tracked-file → needs-human (W3):** a re-verify guardrail mutates a tracked
    file in the integration worktree; assert the harness detects the unexpected modification via
    `git status --porcelain`, **`reset --hard preHead`s back to preHead** (and a unit assertion that
    `merge --abort` would FAIL rc=128 on this exact staged+unstaged path), halts `needs-human` with the
    read-only-violation message, writes no fragment, and consumes no `mergeSequence`. *Pins W3's
    check, not just the doctrine.*
  - **terminal-integration-gate (B2):** (i) a multi-leaf plan with **no** `integrationGate: true`
    sink fails `validate` with `GR2015`; (ii) a multi-leaf plan whose per-task guardrails are all
    narrow and individually green but whose union fails the terminal gate's whole-repo build is
    reported **not green** (the gate task halts `needs-human` on the final HEAD) and
    `--merge-on-success` **refuses to deliver**; (iii) a single-task / single-linear-chain plan with
    no gate marker still validates and runs green (the exemption). *Pins the harness-enforced gate so
    a multi-leaf plan can't drain green against an unbuildable union.*
  - **resume/orphan-prune:** crash mid-run with live worktrees; resume prunes orphan per-task trees,
    **reattaches (does not prune) the integration worktree**, and re-creates fresh per-task
    worktrees off the integration HEAD; assert no stale `guardrails/<oldRun>/<taskId>/<attempt>`
    per-attempt trees survive and the integration worktree + run branch persist. Companion: a task
    that retried leaves **distinct** per-attempt branches; assert `--fresh`'s
    `git branch -D guardrails/<runId>/*` deletes them all (no name collision left a branch behind).
  - **merge-on-success-ff (opt-in delivery):** a wholly-green run with `--merge-on-success` and the
    user's branch *unchanged* since run start; assert the user's branch fast-forwards to the run
    branch tip. Companion: with the user's branch *advanced* mid-run, assert the harness does NOT
    fast-forward, attempts a real merge, and on conflict/failed-gate halts `needs-human` leaving the
    run branch intact (never a force-overwrite).
  - **CRLF byte-identity:** a worktree checkout of a CRLF-containing file under the `.gitattributes`
    policy hashes identically to the integration worktree's bytes (no re-normalization flap).
- **Stage 3 — Independent verification.** Full suite green on 3-OS CI; `guardrails-devils-advocate`
  does a "cheapest wrong merge-back that passes these tests" pass — specifically hunting a
  re-verify that is skippable, a reset that targets the wrong sha, a lock that does not actually
  serialize, **a merge-back that accidentally writes the user's checkout instead of the integration
  worktree**, **a state-fragment merge or `Succeeded` journal write that survives a rolled-back
  merge (B1 split-brain) — i.e. the four effects not actually committing/rolling-back as one**,
  **a terminal integration gate that is declared but never actually re-run on the final HEAD (B2)**,
  and **a resume reconciliation that mis-reads the run-branch trailers** (double-merge or lost-work,
  B3).
- **Stage 4 — Re-dogfood as CAPSTONE.** Regenerate a real plan (e.g. this very feature, or the
  cost-cap dogfood) under the verified harness and run it — from the global tool / `dotnet run`
  Debug build, never the Release self-lock (dogfood-safety memory). Done when the run completes
  green, a human confirms the journal shows ≥2 tasks' actions overlapping in time with merges
  serialized after, **and confirms the run left a `guardrails/run-<runId>` branch with the user's
  original branch untouched** (the dogfood runs with `--merge-on-success` OFF). **The dogfood
  demonstrates; it does not prove.**

This is substantial multi-finding work → the **agent team** (harness-dev + test-author +
skill-author), with hands-on lead review before each stage completes (the standing instruction).

---

## Milestone decomposition (walking-skeleton first)

Each milestone is independently shippable + testable, in DAG order. Test-author tasks precede
implementation (TDD-by-default). The harness runs them autonomously; "milestone" is a logical
unit, not a human gate.

- **M1 — De-serialize the scheduler against a FAKE worktree provider + the executor↔scheduler
  handle seam (SEAM 1) (the walking skeleton).**
  Scope: `IWorktreeProvider` seam + `WorktreeHandle`/`IntegrationHandle` types + a
  `FakeWorktreeProvider` (temp dir per task, no git); **the SEAM-1 signature change** —
  `ITaskExecutor.ExecuteAsync(TaskNode, WorktreeHandle, ct)` — rippled through `ITaskExecutor`,
  `TaskExecutor`, and **every test fake** (`FakeExecutor` in `SchedulerTests`, `RecordingExecutor`
  in `CostCapSchedulerTests`); `Scheduler` creates a "worktree" per task off the (fake) integration
  HEAD and drops the `exclusive` admission gate; a fake merge is a no-op. **Exit:** 3 independent
  prompt-shaped tasks (fake actions) run with overlapping execution windows (gated, not wall-clock)
  — proving the scheduler no longer serializes — with **no git involved**, and the scheduler hands
  each a `WorktreeHandle`. Size: M.
  Agent: `guardrails-harness-developer` (impl) + `guardrails-test-author` (the overlap gate test +
  the fake-executor signature rewrites).
  filesTouched: `Execution/IWorktreeProvider.cs` (new), `Execution/WorktreeHandle.cs` /
  `IntegrationHandle.cs` (new), `Execution/FakeWorktreeProvider.cs` (test double),
  `Execution/ITaskExecutor.cs` (signature), `Execution/TaskExecutor.cs` (accept the handle),
  `Execution/Scheduler.cs`; `tests/**` overlap-gate test + `FakeExecutor`/`RecordingExecutor`
  signature updates. (Note: `WorkspaceLock` deletion + the `TaskNode` triad removal land in M2,
  where the code compiles without them — see M2; M1 leaves `Exclusive` on `TaskNode` read-but-ignored
  if needed to keep M1 small, or removes it with M2.)

- **M2 — Real git worktree lifecycle (`GitWorktreeProvider`) incl. the integration worktree +
  the FIRST half of the triad removal (the part that compiles here).**
  Scope: `GitWorktreeProvider` (create the **integration worktree** on `guardrails/run-<runId>`
  off the user's HEAD at run start; create per-task worktrees off the integration HEAD on per-attempt
  branches; discard --force; prune); `TaskExecutor` cwd → per-task worktree path (via the SEAM-1
  handle); git-required validation (reused **GR2013/GR2014**, message semantics **flipped** from
  capture-escape/restore-without-capture to **not-a-git-repo / MAX_PATH** — a test-message rewrite,
  not a new number) + `.gitattributes` byte-identity policy; the **terminal-gate validation `GR2015`**
  + `TaskNode.IntegrationGate` (§2a); `worktreeRoot` config; **`maxParallelism` default 4 → 2** (a
  real behavior change — every test/fixture that assumed default 4 must be reviewed and re-pinned).
  **Triad removal, part 1 (this milestone, because the code compiles without it once the lock is
  gone):** remove `Execution/WorkspaceLock.cs`, `TaskNode.Exclusive/CaptureHashes/RestoreOnRetry`,
  and the two validators `ValidateCaptureHashPaths`/`ValidateRestoreOnRetry`; **delete/rewrite their
  ~80 test assertions** in `FileHashCaptureTests.cs` is M3's (those couple to the retry loop) — M2
  owns `WorkspaceLockTests.cs` (~10), `PlanValidatorTests.cs` triad rows (~part of 21), and the
  `Exclusive`-related rows in `SchedulerTests`/`PlanLoaderTests`. **No merge-back yet** — tasks run
  in per-task worktrees and are discarded (a single-task or independent-tasks plan works end to end;
  merge is M3). **Exit:** the integration worktree is created off the user's HEAD; a task's
  action+guardrails run inside a real per-task worktree off the integration HEAD; the user's
  branch/HEAD is unchanged after the run (the user-checkout-untouched test); a non-git workspace
  fails validation (GR2013); a multi-leaf plan without an integration-gate sink fails validation
  (GR2015); a CRLF file checks out byte-identical; the suite is green with `WorkspaceLock` and the
  `TaskNode` triad gone. Depends on M1. Size: L (the triad-removal test churn makes it the heaviest
  cleanup milestone alongside M3).
  Agent: `guardrails-harness-developer` + `guardrails-test-author`.
  filesTouched: `Execution/GitWorktreeProvider.cs` (new), `Execution/TaskExecutor.cs`,
  `Execution/Scheduler.cs` (integration-worktree creation at run start; `WorkspaceLock` removal),
  `Execution/WorkspaceLock.cs` (**deleted**), `Loading/PlanValidator.cs` (remove triad validators;
  add git-required + terminal-gate), `Loading/DiagnosticCodes.cs` (GR2013/GR2014 message rewrite +
  GR2015), `Model/TaskNode.cs` (remove triad; add `IntegrationGate`), `Model/RunConfig.cs`
  (`worktreeRoot`, `maxParallelism` default 2); **SSOT §1/§2/§3 git-required + worktree semantics +
  §3 triad-removal (the 25-reference SSOT edit — §3 `exclusive`/`captureHashes`/`restoreOnRetry`
  lines + §3.1/§3.1.1 deletion + §5.3, same-commit contract rule)**; `tests/**`
  (`WorkspaceLockTests.cs` deleted, `PlanValidatorTests.cs` triad rows + GR2013/GR2014 message
  expectations rewritten, `SchedulerTests`/`PlanLoaderTests` `Exclusive` rows, default-4→2 fixtures).

- **M3 — Merge-and-settle (SEAM 2, the B1 atomic unit) + conflict-halt + resume reconciliation +
  `--merge-on-success` + the SECOND half of the triad removal (the soundness milestone).**
  This is the `TaskExecutor`/`AttemptJournaler` refactor it actually is — **NOT "a re-verify
  invocation site."** Scope:
  - **The B1 atomic-unit move:** relocate `AttemptJournaler.CompleteSucceededOrInvalidFragment`'s
    fragment-merge + `ReserveMergeSequence`/consume + `RecordAttempt(Succeeded)` OUT of
    `TaskExecutor.RunAttemptAsync`'s in-worktree success path and INTO the new
    `TaskExecutor.MergeAndSettleAsync` (SEAM 2), after re-verify passes, in the §2 fixed order
    (fragment → git merge commit → journal). On conflict/failed-re-verify/dirty-tracked: rollback,
    journal `needs-human`, write no fragment, consume no sequence.
  - **SEAM 2 wiring:** `MergeAndSettleAsync` commits the per-attempt worktree, calls
    `IWorktreeProvider.MergeBack` (`merge --no-ff <per-attempt-branch>` into the integration worktree
    with the `Guardrails-Task:`/`Guardrails-Run:` trailer), re-verifies via the executor's own
    `GuardrailRunner` against the integration worktree, runs the W3 `git status --porcelain` check,
    then the settle tail. **The `Scheduler` holds `new SemaphoreSlim(1,1)` around the call.**
  - **B3 per-attempt branches** (`guardrails/<runId>/<taskId>/<attempt>`) + **§6 resume
    reconciliation by trailer only** (the second-parent cross-check is NOT implemented).
  - **§2a terminal integration-gate re-verify** against the final HEAD before green; `--merge-on-success`
    refuses without a passing gate.
  - **Opt-in `--merge-on-success`/`mergeOnSuccess`** end-of-run merge (default OFF).
  - **SEAM 3:** `RunReset` gains an `IWorktreeProvider` (orphan prune + worktree wipe + run-branch +
    per-attempt-branch delete).
  - **Triad removal, part 2 (this milestone, because these couple to the retry loop):** remove
    `Execution/CapturedFileStore.cs`, `Execution/FileHashCapture.cs`, and
    `TaskExecutor.RestoreAncestorCaptures`; **delete/rewrite their test assertions** —
    `CapturedFileStoreTests.cs` (~32), `FileHashCaptureTests.cs` (~33), and the restore-on-retry
    rows of `StateFlowTests.cs`/`StateManagerTests.cs` (part of ~113 across those two). With M2's
    part-1 deletions this completes the **~175-assertion / 12-file** triad-test removal/rewrite.
  - **SSOT §5.3** replaced with the integration-worktree merge-back exception (the unified atomic
    unit), and the §3.1/§3.1.1 deletion finalized if M2 left a pointer.
  **Exit:** the clean-merge-but-broken-build test **including the B1 assertions** (needs-human not
  Succeeded, no fragment in `state.json`, `mergeSequence` not consumed, HEAD == preHead, user branch
  untouched), the textual-conflict→abort test, a clean-merge-passes-re-verify test, the
  resume-after-merge-commit-before-journal **and** resume-after-fragment-before-merge-commit tests,
  the reverify-dirties-tracked-file test, the terminal-integration-gate test, and the
  merge-on-success-ff test all pass; independent branches continue past a halted merge; the suite is
  green with `CapturedFileStore`/`FileHashCapture`/`RestoreAncestorCaptures` gone. Depends on M2.
  Size: L.
  Agent: `guardrails-harness-developer` + `guardrails-test-author` (tests FIRST, proving they fail
  against M2's no-merge harness).
  filesTouched: `Execution/MergeBack.cs` (new — or fold into `GitWorktreeProvider`),
  `Execution/TaskExecutor.cs` (`MergeAndSettleAsync`, B1 relocation, drop `RestoreAncestorCaptures`),
  `Execution/AttemptJournaler.cs` (settle tail moves here under the lock), `Execution/Scheduler.cs`
  (`SemaphoreSlim(1,1)` around merge-and-settle, resume pre-pass, terminal-gate re-verify, end-of-run
  merge), `Execution/CapturedFileStore.cs` / `FileHashCapture.cs` (**deleted**),
  `Model/RunConfig.cs` (`mergeOnSuccess`), `Guardrails.Cli` (`--merge-on-success`),
  `State/RunReset.cs` (now takes `IWorktreeProvider`, SEAM 3), SSOT §5.3 + §3.1/§3.1.1 finalize;
  `tests/**` (`CapturedFileStoreTests`/`FileHashCaptureTests` deleted, `StateFlowTests`/
  `StateManagerTests` restore rows rewritten, new merge-and-settle suite).

- **M4 — Skills switch-over (the SKILL-EMITTER triad removal only — the harness triad removal
  already happened in M2/M3).**
  Scope: `plan-breakdown` stops **emitting** the test-protection triad (`captureHashes`/
  `tests-untouched`/`restoreOnRetry`) and `exclusive`/`writeScope` (the harness no longer accepts
  these — they were removed in M2/M3, so a generated folder using them now fails `validate`);
  downstream tasks read upstream's *merged* outputs (`dependsOn` edge + `GUARDRAILS_STATE_IN`);
  **new emitter doctrine:** file-disjoint tasks to minimize conflicts; integration-sensitive tasks
  carry a build/test guardrail (so per-task re-verify catches semantic conflicts); **always emit a
  terminal `integrationGate: true` sink** carrying the whole-repo build/test suite for any
  multi-leaf plan (now a HARD harness requirement, §2a — `validate` errors GR2015 without it); and
  **re-verify-eligible guardrails must be read-only on tracked files** (W3 catalogue doctrine).
  `guardrails-review` challenges overlapping-edit tasks (conflict risk), missing build/test gates on
  integration tasks, and **flags a missing terminal integration gate as a BLOCKER**.
  `guardrails-domain-knowledge` updated (triad gone, worktree model, integration gate, atomic
  merge-and-settle). **Exit:** `/plan-breakdown` on a TDD plan produces no triad, no `exclusive`, a
  terminal `integrationGate` sink, validates clean; review flags a deliberately conflicting pair and
  a deliberately gate-less multi-leaf plan. Depends on M3. Size: M.
  Agent: `guardrails-skill-author`.
  filesTouched: `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`,
  `.claude/skills/guardrails-domain-knowledge/**`, `examples/**` if the golden folder uses the triad.

- **M5 — Dogfood capstone.**
  Scope: regenerate a real plan under the verified harness and run it (global tool / Debug);
  confirm concurrent actions + serialized merges from the journal. **Exit:** green run AND human
  confirmation of ≥2 overlapping actions with merges serialized. Depends on M4 (+ Stage 3
  independent verification). Size: M.
  Agent: `guardrails-skill-author` (regenerate) + lead (runs).

**Dependency summary:** M1 → M2 → M3 → M4 → M5. M1 (de-serialize + SEAM 1) is independently
shippable and demonstrable with zero git; M2 (real worktrees, no merge, + triad-removal part 1 +
the validators/lock/TaskNode cleanup that compiles without merge-back) is independently useful for
independent-task plans; M3 (merge-and-settle SEAM 2 + the B1 atomic unit + triad-removal part 2,
which couples to the retry loop) is the soundness core. **The triad removal is HARNESS work split
across M2 (validators, `TaskNode` triad, `WorkspaceLock`) and M3 (`CapturedFileStore`,
`FileHashCapture`, `RestoreAncestorCaptures` — they couple to the retry loop) — NOT M4.** M4 is left
with only the *skill-emitter* triad removal. The ~175-assertion / 12-file triad-test rewrite and the
25-reference SSOT edit are split the same way (same-commit contract rule: the SSOT lines for a
removed field land in the milestone that removes it).

---

## Schema changes (exact `02-schemas-and-contracts.md` edits)

These are the verbatim SSOT edits, applied in the milestone that implements them (M2/M3),
contract-first.

### §1 Plan folder layout — add the worktree root + git precondition

Add to the layout description:

> **Workspace must be a git repository top-level.** Parallel execution never writes the user's
> checkout. At run start the harness creates a **dedicated integration worktree** on a fresh run
> branch `guardrails/run-<runId>` off the user's current HEAD; this is the sole merge target for
> the run. Each task then runs in its **own** `git worktree` created off the *integration
> worktree's* HEAD (the run branch), so downstream tasks physically see upstream's merged outputs.
> `guardrails validate` and a run pre-flight reject a workspace that is not a git repo top-level
> (`GR2013`, below). The harness creates both kinds of worktree under a **harness-owned root
> outside the workspace** — default `<temp>/guardrails-worktrees/<workspace-hash>/<runId>/`
> (holding `_integration/` and one `<taskId>/` per task), overridable via `guardrails.json:
> worktreeRoot`. The worktree root and the run branch are runtime state: never committed, wiped by
> `--fresh`, pruned on resume (the integration worktree is reattached, not pruned, on resume). The
> user's own working tree and branch are **read-only for the entire run**; the only optional write
> to the user's branch is the end-of-run `--merge-on-success` (§2). The root is deliberately
> outside the workspace so worktree checkouts never appear in the user's `git status` and never
> compound Windows MAX_PATH under a deep plan path.

### §2 `guardrails.json` — add `worktreeRoot` + `mergeOnSuccess`; lower `maxParallelism` default

Change the `maxParallelism` default comment to **2** (worktree mode — each concurrent task is a
full working tree on disk):

```jsonc
  "maxParallelism": 2,                // default 2 in worktree mode (each task = a full worktree)
```

In the jsonc block, add after `workspace`:

```jsonc
  "worktreeRoot": null,               // OPTIONAL; override the git-worktree root (integration + per-task).
                                      //   null (default) = <temp>/guardrails-worktrees/<hash>/<runId>/
  "mergeOnSuccess": false,            // OPTIONAL; if true AND the whole run goes green, merge the run
                                      //   branch guardrails/run-<runId> into the user's original branch
                                      //   at run end (ff-only when possible). Default false = leave for review.
```

Add prose:

> - `worktreeRoot` overrides where git worktrees are created (§1) — both the per-run integration
>   worktree and the per-task worktrees. Absent ⇒ a temp-rooted default outside the workspace. Each
>   task's child processes run with cwd = its per-task worktree; the **integration worktree** (run
>   branch `guardrails/run-<runId>`) is written only by the harness's merge-back (§5.3). The user's
>   own checkout is never written during the run.
> - `mergeOnSuccess` (default `false`; CLI `--merge-on-success` overrides) opts into end-of-run
>   delivery: when the whole run is green, the harness fast-forwards (or, if the user's branch
>   advanced mid-run, attempts a real merge of) the run branch into the user's **original** branch.
>   A conflict, a failed post-merge re-verify, or a dirty user working tree halts to `needs-human`
>   and leaves the run branch intact — `mergeOnSuccess` is never a destructive overwrite. Default
>   `false` leaves the run branch for the user to review and merge by hand (the honest-halt default).
>   `maxParallelism` defaults to 2 here because each concurrent task plus the one integration
>   worktree is a full working tree on disk; raise it on a fast/large `worktreeRoot` volume.

### §3 `tasks/<id>/task.json` — REMOVE `exclusive`/`captureHashes`/`restoreOnRetry`; ADD `integrationGate`

- Delete the `exclusive`, `captureHashes`, and `restoreOnRetry` lines from the jsonc block.
- **Add an `integrationGate` line** to the jsonc block (after `dependsOn`):

```jsonc
  "integrationGate": false,    // optional, default false; marks this task as a terminal whole-repo
                               //   integration gate (§3.3). Its guardrails are re-run against the
                               //   FINAL merged integration HEAD; the run's green verdict is
                               //   contingent on them passing. A multi-leaf plan with no
                               //   integrationGate sink is a GR2015 validation error.
```

- Add a new **§3.3 "Terminal integration gate (`integrationGate`)"**:

> `integrationGate: true` marks a task as the **whole-repo gate on the merged union**. The harness
> cannot infer "this guardrail is whole-repo" by inspection, so the gate is explicitly declared.
> **Validation:** a **multi-leaf** plan (≥2 DAG sinks, or any non-linear plan whose tasks could
> merge an uncertified union) must have **at least one `integrationGate: true` task that is a DAG
> sink** (no task depends on it) — otherwise `validate` fails with **`GR2015`**. Single-task plans
> and single-linear-chain plans (exactly one sink whose own re-verify already ran against the only
> HEAD) are **exempt**. **Run semantics:** after the scheduler drains, before the run may report
> green, the harness re-verifies every `integrationGate` task's guardrails against the **final
> integration HEAD** (the whole merged union); a failure halts that gate task `needs-human` and the
> run is **not** green. `--merge-on-success` refuses to deliver a run with no passing integration
> gate. Re-verify here uses the same `GuardrailRunner` against the integration worktree as the
> per-task merge-back re-verify (§5.3) — the gate task is simply the sink whose guardrails are
> declared whole-repo and whose pass is a run-green precondition.

- Delete §3.1 (`captureHashes`) and §3.1.1 (`restoreOnRetry`) **in full** — physical isolation
  replaces the hash-based test-protection triad. Add a one-line pointer where §3.1 was:

> *(Former §3.1/§3.1.1 — the `captureHashes`/`restoreOnRetry` test-protection triad — are removed.
> Test files are protected by physical isolation: each task edits only its own worktree, and a
> task that does not own a test file cannot merge a change to it without a conflict halting to a
> human. See §5.3 merge-back.)*

- Add a new **§3.2 "Worktree task semantics"**:

> The harness creates one **integration worktree** per run (run branch `guardrails/run-<runId>`,
> off the user's HEAD at run start) — the sole merge target. Every task then runs its action and
> guardrails inside its **own git worktree**, created off the *integration worktree's* current HEAD
> when the task becomes ready. The task's `cwd` (the `workspace` child processes see) is the
> per-task worktree, not the user's checkout. A failed attempt's worktree is discarded and a fresh
> one created off the current integration HEAD for the retry (so a retry starts pristine *and* sees
> any siblings merged in the interim). On success the harness commits the worktree's changes and
> merges them into the integration worktree (§5.3). A task that depends on another reads the
> producer's **merged** outputs: its worktree is created off an integration HEAD that already
> contains the producer's merge commit, so the produced files are physically present, and produced
> state is available via `GUARDRAILS_STATE_IN` as today. No cross-task `actionExitCode` channel
> exists. The user's own checkout is never written during the run; the run branch's first-parent
> merge commits are the durable record used on resume (§7-resume).

### §5.3 — replace "exactly one case" with the integration-worktree merge-back exception

Replace the §5.3 indented contract block with:

> **The harness writes only the harness-owned integration worktree (run branch
> `guardrails/run-<runId>`), via merge-back, after a task's action and guardrails succeed in its
> per-task worktree — and never otherwise. The user's own checkout is read-only for the entire
> run.** Each task runs in an isolated per-task git worktree (§3.2); the harness is read-only on
> both the user's checkout and every per-task worktree it does not own.
>
> **The atomic unit is the whole settle tail — state-fragment merge + git merge-back + post-merge
> re-verify + journal `Succeeded` + `mergeSequence` consumption — committed together or rolled back
> together, all under a single serialize-merges lock (`SemaphoreSlim(1,1)`).** Nothing is journaled
> `Succeeded`, no fragment reaches `state.json`, and no `mergeSequence` is consumed until the merged
> bytes pass re-verify. (This supersedes the prior model where the executor merged the fragment and
> journaled `Succeeded` in its in-worktree success path *before* any merge-back — which would leave a
> task journaled succeeded with a fragment its integration HEAD does not reflect when a re-verify
> fails.) One merge into the integration worktree at a time:
>
> 1. record `preHead = git -C <integration> rev-parse HEAD` (the rollback point on the run branch);
> 2. commit the per-task worktree's changes on the **per-attempt** branch
>    `guardrails/<runId>/<taskId>/<attempt>`;
> 3. **two-phase merge** — `git -C <integration> merge --no-commit --no-ff <per-attempt-branch>`
>    produces the merged *bytes in the working tree* but **does NOT create the merge commit yet**, so
>    the resume authority (the commit + its trailer) does not exist until step 6;
> 4. **on textual conflict:** `git -C <integration> reset --hard preHead` (integration worktree returns to
>    `preHead`); journal `needs-human`, write **no** fragment, consume **no** `mergeSequence`;
>    worktree preserved for inspection; dependents blocked;
> 5. **on a clean textual merge:** re-run *this task's* guardrails against the **integration
>    worktree's merged bytes** (the bytes that ship), then assert `git -C <integration> status
>    --porcelain` shows only the expected staged merge and no unexpected **tracked** modification
>    (re-verify must be read-only on tracked files). **Any re-verify fail, or a dirtied tracked
>    file:** `git -C <integration> reset --hard preHead` (discard the staged, uncommitted merge — returns to
>    `preHead`); journal `needs-human` (reason: "merged cleanly but failed re-verify on the
>    integration tree", or "re-verify dirtied a tracked file"); write **no** fragment, consume **no**
>    `mergeSequence`; worktree preserved; dependents blocked;
> 6. **on full success, the settle tail in this FIXED order** (committed together or not at all):
>    (i) deep-merge the task's fragment into `state.json`; (ii) `git -C <integration> commit` to
>    create the merge commit carrying the parseable `Guardrails-Task: <taskId>` / `Guardrails-Run:
>    <runId>` trailer (the durable resume authority, §7-resume); (iii) consume the `mergeSequence` +
>    journal `Succeeded`. The fragment merge precedes the git **commit** (deferred via `--no-commit`)
>    so the resume pre-pass can never treat a task succeeded-by-merge-commit while its state is
>    missing — the whole settle tail (state + git + journal) is the atomic unit, all under the lock.
>
> A clean textual merge is **necessary but not sufficient** — git's no-conflict result does not
> certify that the union of two independently-verified changes builds (a semantic conflict). The
> only sound gate is re-verifying the merged bytes, which the `--no-commit` two-phase merge makes
> possible *before* any authoritative commit exists. Every non-success path is a single `git
> -C <integration> reset --hard preHead` (restores the index + working tree and clears
> `MERGE_HEAD`), which leaves the integration worktree at exactly `preHead` **and leaves
> `state.json`, the journal, and the `mergeSequence` untouched**: **a failed merge or re-verify
> leaves state, git, and journal all UNCHANGED, never half-merged — and the user's checkout is never
> touched at all.** (`reset --hard preHead` is used rather than `merge --abort` because `merge
> --abort` fails `rc=128 "Entry not uptodate"` on the W3 dirtied-tracked-file path; `reset --hard`
> works in all cases.) A git or IO failure during merge-back is an actionable
> `needs-human` halt routed through the normal failed path, never an uncaught throw (the run does not
> abort over one merge).
>
> Independent branches continue past a halted merge; the serialize lock is released on every exit
> path. Git's own exit codes drive the harness's *control flow* here (conflict detection) but are
> never a *guardrail* verdict — the task's pass/fail still comes from its guardrail outcomes
> (deterministic exit / prompt verdict file) re-run on merged bytes.
>
> **Run end (opt-in delivery).** When the run drains wholly green AND `mergeOnSuccess`/
> `--merge-on-success` is set, the harness brings the run branch into the user's **original** branch
> (captured at run start): `git merge --ff-only guardrails/run-<runId>` when the user's branch has
> not advanced, else a real `git merge` whose own re-verify must pass. A conflict, a failed
> post-merge re-verify, or a dirty user working tree halts to `needs-human` with the run branch left
> intact — never a force-overwrite. With `mergeOnSuccess` absent (the default), the run branch is
> left untouched for the user to review and merge. This end-of-run merge is the **only** write the
> harness ever makes to the user's branch, and only on an explicit opt-in after a wholly-green run.

Update the trailing sentence ("Any new capability that needs the harness to write workspace
files…") to point at this integration-worktree merge-back (plus the opt-in end-of-run merge) as the
sole exceptions.

### New diagnostic codes (`DiagnosticCodes.cs` + §3 task.json note)

Master's validation codes end at **GR2014**. The triad codes (GR2013 `captureHashes`-escape,
GR2014 `restoreOnRetry`-without-captureHashes) are **removed** with the triad. **Reusing the freed
GR2013/GR2014 numbers is SOUND** — they are occupied by the triad on master and freed by its removal
(M2), so no external consumer collides. The git-required gate takes them; the terminal-integration-
gate validation takes a **separate fresh number, GR2015** (B2 is a distinct concern, not a reuse):

- **`GR2013` (error) — workspace is not a git repository top-level.** "The workspace
  '<path>' is not a git repository top-level. Worktree-per-task parallel execution requires the
  workspace to be a git repo (`git init` it, or point `workspace` at the repo root)."
  *(Test-message rewrite: the GR2013 expectation flips from capture-path-escape to not-a-git-repo —
  the `PlanValidatorTests` rows asserting the old message must be rewritten in M2.)*
- **`GR2014` (warning) — potential Windows MAX_PATH risk.** Optional pre-flight warning when
  `<worktreeRoot>/<longest-taskId>/<deepest-tracked-path>` would exceed 260 chars on Windows
  without `core.longpaths`. Warning, not error (the plan may run on Linux).
  *(Test-message rewrite: GR2014 flips from restore-without-capture to MAX_PATH — M2.)*
- **`GR2015` (error) — multi-leaf plan with no terminal integration gate (B2, §3.3).** "Plan
  '<name>' has <k> independent leaves/sinks but no task marked `integrationGate: true` on a DAG
  sink. A multi-leaf plan needs a terminal whole-repo gate that re-verifies the merged union; add an
  `integrationGate: true` sink (e.g. a build/test-the-whole-repo task) depending on the leaves."
  Single-task / single-linear-chain plans are exempt (no union to break). This is a **fresh
  number**, NOT a reuse — B2's gate is orthogonal to the git-required check.

(Removing `ValidateCaptureHashPaths`/`ValidateRestoreOnRetry` and their tests is part of the M2
change; the GR2013/GR2014 *numbers* are re-documented to the new meanings — acceptable because the
triad never shipped to any external consumer, only the author depends on the tool.)

---

## Devil's-advocate self-critique

The prior design false-greened. So I am my own harshest critic here — specifically on "where could
this ALSO look sound but not be?"

**0. (THE BLOCKER THE PRIOR SELF-CRITIQUE NEVER LOOKED AT — B1.) "Your §2 rolls back GIT on a failed
re-verify, but the executor writes `state.json` and journals `Succeeded` in its in-worktree success
path, BEFORE the merge-back runs. So a failed post-merge re-verify resets git to `preHead` but
LEAVES the task journaled `Succeeded` with its fragment in `state.json` — a downstream task reads
state keys the integration HEAD doesn't reflect. That is the plan-05 split-brain, re-entered through
the seam you waved past with 'harness still deep-merges fragments, unchanged.'"**
*Conceded as the sharpest miss of the prior draft, and now ADDRESSED by making the atomic unit the
whole tail.* The fix (§2, §5.3): the fragment merge, the `mergeSequence` consumption, and the
`Succeeded` journal write **move out of the executor's in-worktree success path and into the
merge-back critical section, after re-verify passes** — committed together with the deferred git
merge commit, or rolled back together with it (the `--no-commit` two-phase merge means the merge
commit, the resume authority, is created only at the very end). On conflict or failed re-verify:
`reset --hard preHead`, write no fragment, consume no sequence, journal `needs-human`. The
`clean-merge-but-broken-build` test now asserts all four (needs-human not Succeeded; no fragment in
`state.json`; `mergeSequence` not consumed; HEAD == preHead) — it pins exactly this.

**But is the trio ACTUALLY atomic across state + git + journal? NO — and I will not let the revision
pretend it is.** There is no shared transaction over three persistent stores (git ODB/refs,
`state.json`, `run.json`). What makes it *sound* is a **single serialized commit ORDER plus one
durable authority** the resume pre-pass reconciles to — not atomicity. The load-bearing choice (the
NEW residual this fix surfaces, named not buried): **the state-fragment merge is ordered BEFORE the
git merge commit.** Because the git merge commit's trailer is the resume authority (§6), any task
the pre-pass treats succeeded-by-merge-commit already has its fragment durably in `state.json`. The
reverse order would re-introduce B1 in miniature — a crash between the git commit and the fragment
write would re-green a task whose state keys are missing. The residual that genuinely survives:
**a crash in the narrow window after the fragment write but before the git commit leaves a fragment
in `state.json` for a task the run branch does not yet reflect.** This is bounded and self-healing,
NOT the plan-05 disease: (i) it is the task's OWN namespace (single-writer-per-key, §6.2), (ii) the
producer is re-run on resume (it is not in the merged set) and the fragment re-merges idempotently,
(iii) no downstream task ever ran against it — downstream worktrees are cut off the integration
HEAD, which lacks the un-committed merge. So the honest claim is **not** "atomic," it is "serialized
+ ordered + idempotent-on-replay, with one self-healing transient that never reaches a consumer."
That is the strongest honest statement, and it is strictly sound where the prior draft was not.

**1. "Post-merge re-verify is the WHOLE soundness claim, and it is only as good as the merging
task's guardrails. A task with weak guardrails (or whose guardrails don't exercise the broken
interaction) merges a semantic conflict that re-verify passes — green but unsound. You have moved
the false-green from the enforcer to the guardrail-coverage gap."**
*Conceded — this is the sharpest objection and the honest residual.* Re-verify catches a semantic
conflict **only if** the merging task's guardrails transitively exercise the broken interaction.
A task whose guardrails check only its own subtree, merged against a sibling it semantically
collides with, can pass re-verify while the union is broken. I do **not** claim this is closed.
What bounds it — **and this is now STRONGER than the prior draft (B2):** (i) the
*integration-sensitive* tasks are exactly the ones `plan-breakdown` gives a whole-repo build/test
guardrail, and a whole-repo build re-run on the merged integration worktree **does** catch "the
union doesn't compile/test"; (ii) the terminal whole-repo gate (a `integrationGate: true` sink) is
**no longer just skill doctrine — it is a HARD harness requirement (§2a)**: a multi-leaf plan with
no integration-gate sink fails `validate` (GR2015), and the run's green verdict is contingent on the
gate's guardrails passing on the FINAL merged HEAD. So the prior draft's weakest spot — "a plan with
weak per-task guardrails AND no terminal gate" — is **closed for the 'no terminal gate' half by
construction** (you cannot have a multi-leaf plan without one). So the design's honest claim is now:
**the merge ships only bytes that passed the merging task's guardrails on the actual merged tree,
AND a harness-mandated terminal gate re-verifies the whole union before green** — not "every
semantic conflict is impossible." **Where this objection still wins (the surviving residual):** the
gate is only as strong as the gate task's *own* guardrails — a plan author can mark a sink
`integrationGate: true` whose guardrails are weak (a trivial `exit 0`). The harness enforces that a
gate *exists* and *runs on the final HEAD*; it cannot enforce that the gate is *thorough*. That
residual is real and is `guardrails-review`'s job (flag a weak gate), not a harness guarantee —
stated, not oversold. This is strictly stronger than plan 05 (which verified *nothing* on the
shipped bytes) and stronger than the prior draft (where the gate could be omitted entirely).

**2. "`reset --hard` (the rollback) assumes the merge target is clean and unraced. If ANYTHING
else touches it — a guardrail that writes it, a stray process, the user's editor — the reset
destroys uncommitted work or the abort fails. You have a hidden 'pristine merge-target'
precondition."**
*LARGELY DISSOLVED by the integration-worktree model — but a reduced residual genuinely survives,
and I will not pretend it is fully gone.* The original objection had two halves, and the
integration worktree kills both of the worst ones:
- **"The user edits mid-run"** — gone. The merge target is the harness-owned integration worktree,
  not the user's checkout. The user's editor never touches it; the user can edit their own checkout
  freely (it is read-only *to the harness*, not frozen *to the user*) without any effect on the
  merge target. (The one consequence of the user committing to their *branch* mid-run is a separate,
  new failure mode — counter 6 — not this one.)
- **"Build artifacts dirty the merge target"** — no longer touches the user's repo at all; any
  `bin/`/`obj/` churn happens in the disposable integration worktree.
- **THE RESIDUAL THAT SURVIVES — now a CHECK, not just a hope (W3):** a re-verify guardrail must
  still be **read-only on the *tracked* files of the integration worktree**. If a re-verify guardrail
  mutates a tracked file (a "fix-up" that rewrites source as a side-effect of "verifying"), the
  uncommitted tracked change would dirty the next task's merge. **The prior draft left this as
  doctrine — a hope. It is now ENFORCED:** after a passing re-verify the harness asserts `git -C
  <integ> status --porcelain` shows no tracked modification beyond the expected staged merge; if it
  does, the harness `reset --hard preHead`s (back to preHead) and halts `needs-human` with a clear
  read-only-violation message (§2, §5.3). An honest halt, not silent corruption. Artifacts a
  re-verify produces (`bin/`, `obj/`) must be git-ignored so they are not "tracked modifications" —
  treated as a checked setup precondition where feasible, with the `git status` assertion as the
  runtime backstop. This residual is now
  *detected and halted*, not merely hoped-avoided — the strongest honest statement available. The
  integration worktree shrank counter-2 (user-edits and user-repo-artifacts halves are gone); W3
  converts the last sliver from a hope into a check.

**3. "Disk and MAX_PATH are not edge cases — they are the COMMON case for the tool's likely users
(large .NET repos on Windows). N worktrees of a multi-GB repo at parallelism 8 is 8× the working
set; a deep repo under a temp root blows 260 chars. You will get adoption-blocking failures on day
one, and 'it works on my small example' is exactly the wall-clock false-confidence you warned
about."**
*Partially conceded — it sets the v1 default conservatively, and the integration worktree makes
the footprint slightly WORSE (an honest +1).* The honest responses: (i) `git worktree` shares
`.git/objects`, so it is (N+1)× the *working tree*, not (N+1)× the repo — real but smaller than the
objection implies; (ii) the integration worktree is a deliberate **+1 tree** (the cost of never
writing the user's checkout) — I am not hiding it; (iii) the **default `maxParallelism` for
worktree mode is set to 2** so the out-of-box experience is 3 trees (2 tasks + 1 integration), not
9, and the user raises it on a large/fast volume; (iv) MAX_PATH is mitigated by the short temp root
+ a pre-flight warning (GR2014) + documenting `core.longpaths`. **Where this wins:** on a genuinely
large Windows repo, worktree-per-task at high parallelism is *expensive*, and the integration
worktree adds one more tree to that cost; the v2 mitigations (pooling / sparse / CoW) are not v1.
The honest framing: v1 worktree-per-task is *sound* and *correct* but *not yet cheap on large
repos*, and the integration worktree trades one extra tree's disk for never racing the user's
checkout — and I am stating both as named limits, not burying them. This is the opposite of
overselling.

**4. "You replaced one home-grown mechanism (the enforcer) with another (record-preHead /
merge / re-verify / reset orchestration). That orchestration is ALSO 'a self-implemented
likely-to-have-bugs solution' — the very thing the product owner objected to."**
*Partially conceded, and it sharpens the seam design.* The objection has teeth: the *orchestration*
(when to abort, when to reset, lock discipline, re-verify placement) is harness code we write and
can get wrong. But the crucial difference from the enforcer: **every primitive doing the dangerous
work is git's** — `merge`, `merge --abort`, `reset --hard`, `worktree add/remove` are
battle-tested; we never re-implement isolation, diffing, or rollback. The enforcer *re-implemented*
the isolation/rollback primitive (snapshot/diff/revert) from scratch; this design *sequences* git's
primitives. The bug surface is the *sequencing*, which is exactly what the Stage-2 tests pin
(clean-merge-broken-build asserting the integration worktree's run-branch `HEAD == preHead`, the
user-checkout-untouched test, the resume-after-merge-commit test, the "cheapest wrong merge-back"
Stage-3 pass). It is a smaller, more testable surface than a hand-rolled predicate-locking
transaction manager — but it is **not zero**, and pretending the orchestration is risk-free would
repeat the overselling. The integration worktree adds a little sequencing surface (create-at-start,
reattach-on-resume, opt-in merge-at-end), which the new tests cover. The mitigation is the
test-first discipline (§Process), not a claim of inherent safety.

**5. "Where else could this look green but be unsound? The resume reconciliation: a crash between
the merge commit and the journal write. You hand-waved 'the merge commit is the durable record' —
but if the resume pre-pass mis-reads that, it either re-runs a merged task (double-merge → conflict
against itself) or skips an un-merged one (lost work)."**
*RESOLVED — but the prior draft's detection mechanism was itself unsound, and B3 fixes it.* The
prior draft proposed cross-checking the `Guardrails-Task:` trailer against **"the merged branch ref
recorded in the commit's second parent."** That cross-check is **illusory and is now DROPPED (B3):**
a git merge commit stores its parents as **SHAs, not ref names** — there is no
`guardrails/<runId>/<taskId>` *ref* to read off the second parent — and the per-task branches are
deletable by `--fresh`, so a ref that existed at merge time may be gone at resume time. Pinning
resume correctness to a non-existent ref read would have been a latent bug. **The sound rule (§6) is
trailer-only:** the run branch `guardrails/run-<runId>`'s first-parent merge commits ARE the durable
record; each carries the `Guardrails-Task:`/`Guardrails-Run:` trailer **the harness itself wrote** on
a branch *it* owns and only appends to. Build the *merged set* from those trailers; **a task in the
merged set is treated `succeeded` regardless of the journal** (the merge commit beats a lost journal
write — and, B1 corollary, its fragment is already in `state.json` because the fragment merge is
ordered before the commit); **a task with no merge-commit trailer, journaled `running`, is reset to
`pending` and re-run** (its orphan per-attempt worktree pruned first). **Double-merge is impossible
by construction:** a merged task is treated succeeded → never re-attempted → no new per-attempt
branch is ever created or re-merged for it. This rests on the **pre-pass alone** (sound), no longer
on any second-parent cross-check. A second B3 fix removes a different latent bug: **each attempt gets
a DISTINCT branch `guardrails/<runId>/<taskId>/<attempt>`**, so retried attempts never collide on a
branch name, and the resume walk, the orphan prune, and `--fresh`'s `git branch -D
guardrails/<runId>/*` are all coherent. The tests `resume-after-merge-commit-before-journal` (trailer
read even after the attempt ref is deleted) and `resume-after-fragment-before-merge-commit` (B1
reverse window) pin both. **The one residual, named honestly:** the trailer is authoritative only
within the documented integrity boundary — **the user does not rewrite the harness-owned run
branch**. A user who hand-edits the run branch mid-resume is outside the contract (documented, not
silently handled). This is the opposite of plan 05's wave-through: a specified, tested rule resting
on a real durable record, with its one boundary stated.

**6. (NEW — introduced by the integration-worktree model itself.) "You traded one race for another.
The user's-checkout-as-target model wrote the user's live tree, so it was always integrated against
*current* state — at the cost of racing the user's editor. Your integration worktree forks off the
user's HEAD at run start and never re-bases. If the user commits to their own branch mid-run, the
whole run integrated against a STALE base, and `--merge-on-success` either silently delivers an
out-of-date merge or fails. And the end-of-run merge is itself a `git merge` that can conflict —
the very thing you said v1 sends to a human."**
*Conceded as a genuinely new failure mode — this is the residual the integration-worktree model
introduces, and I am naming it rather than letting the revision quietly drop the adversarial rigor.*
Two distinct hazards, both handled but neither free:
- **Stale base (run branch forked behind the user's branch).** The run integrated against the HEAD
  at run start. If the user advances their branch mid-run, the run branch is now behind. This is
  *not unsound* for the run itself — every task was still re-verified against the integration
  worktree's actual bytes, which is the soundness core — but the *delivered* result is "the run's
  changes layered on the start-of-run base," which may not be what the user wants on top of their
  new commits. **Handling:** the default (leave for review) makes this the user's explicit merge
  decision — they see `git diff <their-branch>...guardrails/run-<runId>` and decide. With
  `--merge-on-success`, a fast-forward is impossible (their branch moved), so the harness does a
  real merge whose result is **re-verified by a final whole-repo gate**, and a conflict or failed
  gate halts to `needs-human` — never a silent stale delivery.
- **The end-of-run merge can itself conflict.** True — and it is the one place v1 *does* run a merge
  that can hit a human. But it is bounded: it happens **at most once per run**, **only on explicit
  `--merge-on-success`**, **only after a wholly-green run**, and **never force-overwrites** (a
  conflict leaves the run branch intact with a manual-merge message). This is consistent with "no AI
  auto-resolve in v1": the end-of-run conflict goes to the human exactly like a per-task conflict
  does. The default-OFF means the *common* path never runs this merge at all.
- **Honest residual carried forward:** with `--merge-on-success` ON and a user who commits to their
  branch mid-run, the end-of-run merge is the one moment the run touches the user's branch and can
  need a human — and a never-re-based run branch can deliver start-of-run-based changes. A v2
  "re-base the run branch on the user's branch before delivery" (or a "user's branch must not move
  during a run" advisory) would tighten this; it is named, not built, for v1. The default-OFF
  posture means v1 ships the *safe* behavior by default and the *convenient-but-sharper* behavior
  only on opt-in.

**Net:** the design survives self-critique with the claim scoped honestly — **physical isolation
(real, free, from git) + the user's checkout is never written + the whole settle tail
(state+git+journal) commits or rolls back as one serialized ordered unit + a harness-mandated
terminal gate re-verifies the union + conflicts halt to human** — and the residuals carried into the
handoff:
- **B1 (counter 0) — the split-brain the prior critique missed — is addressed** by moving the
  state-fragment merge, `mergeSequence`, and `Succeeded` journal write into the merge-back critical
  section after re-verify. **Honest about the limit of the fix:** the trio is **not truly atomic**
  (three stores, no shared transaction); it is *serialized + ordered + idempotent-on-replay*, with
  one self-healing transient (a fragment can briefly exist for a not-yet-committed task — own
  namespace, producer re-runs, no consumer ever sees it). The load-bearing ordering choice
  (fragment **before** the git commit) is what keeps it sound; reversing it would re-introduce B1.
- **(counter 1) re-verify is only as strong as the merging task's guardrails** — but the terminal
  whole-repo gate is **now a HARD harness requirement (B2):** a multi-leaf plan cannot omit it
  (GR2015) and the run is not green until it passes on the final HEAD. Surviving residual: the
  harness mandates the gate *exists and runs*, not that it is *thorough* (`guardrails-review`'s job).
- **(counter 2) re-verify must be read-only on tracked files — now a CHECK, not a hope (W3):** the
  harness asserts `git status --porcelain` after re-verify and halts `needs-human` on a dirtied
  tracked file. The user-edits and user-repo-artifact halves are gone (integration worktree); the
  last sliver is detected and halted, not merely hoped-avoided.
- **(counter 3) disk/MAX_PATH** make v1 sound but not cheap on large Windows repos — honest +1 tree
  for the integration worktree, default `maxParallelism` **lowered 4→2** (a real behavior change),
  **plus N2: every retry pays a full worktree checkout**; v2 mitigations (pooling/sparse/CoW) named
  not built.
- **(counter 4) orchestration risk** is bounded by leaning on git's primitives + test-first, but
  the B1/SEAM-2 move (state+journal under the lock) and the W3 check **add sequencing surface** —
  more to get wrong, all pinned by the new tests.
- **(counter 5) resume reconciliation** is now a resolved, tested rule resting on the
  **trailer ALONE (B3)** — the prior draft's second-parent-ref cross-check was illusory (merge
  commits store SHAs, not ref names; per-attempt branches are deletable) and is dropped; per-attempt
  branch names remove the retry-collision bug. Residual: the user must not rewrite the run branch.
- **(counter 6) NEW** — a run branch can go stale if the user commits to their branch mid-run, and
  opt-in `--merge-on-success` is the one place v1 runs a merge that can need a human; handled by
  default-OFF + a final gate + never-force, with re-base as a named v2 tightening.
- **NEW residual surfaced by encoding the fixes (W2, honest):** the serialized merge tail is **the
  full guardrail suite re-running per task, single-file under one global lock**, plus the terminal
  gate's full-suite run — a real throughput ceiling the prior "only the merge tail serializes"
  framing understated. Parallelism is in *generation*, not *integration verification*.

---

## Implementation handoff (agent + filesTouched + sequencing)

Sequenced; later milestones depend on earlier. (Detail in §Milestones.)

| Stage / Milestone | Agent | filesTouched (primary) |
|---|---|---|
| Stage 1 — SSOT contract | `guardrails-architect` proposes, lead applies | `docs/plans/02-schemas-and-contracts.md` (§1 integration worktree + git precondition; §2 `worktreeRoot`/`mergeOnSuccess`/`maxParallelism` default 2; §3 remove triad + add `integrationGate`; §3.2 worktree semantics; §3.3 terminal integration gate; §5.3 integration-worktree merge-back as the **unified state+git+journal atomic unit**; reused GR2013/GR2014 + fresh **GR2015**; the 25-reference triad-removal edit), this doc |
| M1 — de-serialize + SEAM 1 (executor↔scheduler handle) vs fake provider | `guardrails-harness-developer` + `guardrails-test-author` | `Execution/IWorktreeProvider.cs`, `Execution/WorktreeHandle.cs`/`IntegrationHandle.cs`, `Execution/FakeWorktreeProvider.cs`, `Execution/ITaskExecutor.cs` (signature +`WorktreeHandle`), `Execution/TaskExecutor.cs`, `Execution/Scheduler.cs`, `tests/**` (`FakeExecutor`/`RecordingExecutor` signature rewrites + overlap gate) |
| M2 — real git worktree lifecycle (incl. integration worktree) + triad-removal **part 1** | `guardrails-harness-developer` + `guardrails-test-author` | `Execution/GitWorktreeProvider.cs`, `Execution/TaskExecutor.cs` (cwd→worktree), `Execution/Scheduler.cs` (integration worktree at run start; **delete `WorkspaceLock`**), `Execution/WorkspaceLock.cs` (**deleted**), `Loading/PlanValidator.cs` (remove triad validators; git-required + terminal-gate), `Loading/DiagnosticCodes.cs` (GR2013/GR2014 message rewrite + **GR2015**), `Model/TaskNode.cs` (remove triad; add `IntegrationGate`), `Model/RunConfig.cs` (`worktreeRoot`, `maxParallelism` **4→2**), SSOT §1/§2/§3 (incl. 25-ref triad removal), `tests/**` (`WorkspaceLockTests` deleted, `PlanValidatorTests` GR2013/2014 message + triad rows, default-4 fixtures) |
| M3 — merge-and-settle (SEAM 2, **B1 atomic unit**) + conflict-halt + resume (**B3 trailer-only**) + terminal gate re-verify + `--merge-on-success` + triad-removal **part 2** + **SEAM 3** | `guardrails-harness-developer` + `guardrails-test-author` (tests FIRST) | `Execution/MergeBack.cs` (or fold into provider), `Execution/TaskExecutor.cs` (`MergeAndSettleAsync`, **relocate fragment-merge+`mergeSequence`+`Succeeded` here**, drop `RestoreAncestorCaptures`), `Execution/AttemptJournaler.cs` (settle tail under lock), `Execution/Scheduler.cs` (`SemaphoreSlim(1,1)`, resume pre-pass, terminal-gate re-verify, end-of-run merge), `Execution/CapturedFileStore.cs`/`FileHashCapture.cs` (**deleted**), `Model/RunConfig.cs` (`mergeOnSuccess`), `Guardrails.Cli` (`--merge-on-success`), `State/RunReset.cs` (**now takes `IWorktreeProvider`**), SSOT §5.3 + §3.1/§3.1.1 finalize, `tests/**` (`CapturedFileStoreTests`/`FileHashCaptureTests` deleted, `StateFlowTests`/`StateManagerTests` restore rows, new merge-and-settle + B1 + B3 suites) |
| M4 — skills switch-over (**skill-emitter** triad removal only) | `guardrails-skill-author` | `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`, `.claude/skills/guardrails-domain-knowledge/**`, `examples/**` |
| M5 — dogfood capstone | `guardrails-skill-author` regenerate, lead runs | a regenerated plan folder; run journal |
| Stage 3 — independent verification | `guardrails-devils-advocate` + lead | (review only) |

**Triad removal is HARNESS work split M2/M3, NOT M4** (verified: the triad is fully wired on
`feat/worktree-per-task` — ~175 test assertions across 12 files, ~25 SSOT references):
- **M2** (compiles without merge-back): delete `Execution/WorkspaceLock.cs`,
  `TaskNode.Exclusive/CaptureHashes/RestoreOnRetry`, `ValidateCaptureHashPaths`,
  `ValidateRestoreOnRetry`; rewrite the SSOT §3 lines + §3.1/§3.1.1 (start) + the
  `WorkspaceLockTests`/`PlanValidatorTests`/`SchedulerTests` triad assertions.
- **M3** (couples to the retry loop): delete `Execution/CapturedFileStore.cs`,
  `Execution/FileHashCapture.cs`, `TaskExecutor.RestoreAncestorCaptures`; rewrite
  `CapturedFileStoreTests`/`FileHashCaptureTests` (deleted) + the restore rows of
  `StateFlowTests`/`StateManagerTests`; finalize the SSOT §3.1/§3.1.1 deletion.
- **M4**: removes only the *skill emitter's* triad output (no harness code).

---

## Proposed plan-document edits

I propose (you approve, then I apply):

1. **`docs/plans/07-worktree-per-task.md`** — this document (written to the worktree, **not
   committed**).
2. **`docs/plans/02-schemas-and-contracts.md`** — the §Schema-changes above, applied verbatim in
   the same change as the M2/M3 harness work begins (Stage 1). Until then they live here as the
   spec.
3. **`docs/plans/03-roadmap.md`** — promote v2 bet #1 (worktree-per-task) to **active /
   in-progress** with a pointer to this plan 07; keep the #54 (mechanics) issue reference and move
   **#57 (AI merge-conflict resolution) to a still-deferred v2 sub-bet** (conflicts → human in
   v1). Add a v2 sub-bet for **re-basing the run branch on the user's branch before
   `--merge-on-success` delivery** (counter 6's named-not-built tightening). Rewrite risk-register
   item #2 ("parallel tasks sharing one workspace") to: "each task runs in its own git worktree
   (plan 07); the harness merges into a dedicated integration worktree on run branch
   `guardrails/run-<runId>` (the single writer); the user's checkout is never written (optional
   `--merge-on-success` delivers at run end); conflicts halt to needs-human (no AI auto-resolve in
   v1)." Drop the `exclusive`-by-default mention (the field is removed). Note the worktree-mode
   `maxParallelism` default of 2. Add a line recording **plans 05/06 as the rejected disjoint-scope
   attempt** (false-greened, delivered only self-escape revert not mutual isolation — superseded by
   physical isolation here).
4. **Plans 05/06 themselves** live only on the abandoned `feat/disjoint-scope-ownership` branch and
   are not merged; this plan 07 does not need to edit them. If they are ever brought onto master for
   the historical record, add a banner: *"Rejected approach — superseded by plan 07
   (worktree-per-task). Disjoint-scope on a shared workspace delivered self-escape revert, not
   mutual isolation, and false-greened (#88). Physical isolation via git worktrees replaces it."*

No code, no commits — design only.
