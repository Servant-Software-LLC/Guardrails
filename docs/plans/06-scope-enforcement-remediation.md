# Plan 06 — Scope-Enforcement Remediation (design-of-record)

> **Status:** remediation design-of-record for the disjoint-scope / `writeScope` feature
> (branch `feat/disjoint-scope-ownership`, dogfood plan 05). The agent team reviewed plan 05
> and filed it **UNSOUND, do-not-ship**: six issues — blockers **#88** (whole-workspace
> enforcer corrupts concurrent siblings), **#89** (`IsInScope` glob soundness), **#90**
> (`workingDirectory` base mismatch), **#91** (stale-branch reconciliation), **#92** (Reality
> Gate unmet + missing end-to-end tests) and the **#93** WEAK/NIT roll-up. This document
> resolves each with a concrete, reviewable decision and is the input the
> `guardrails-harness-developer` / `guardrails-test-author` / `guardrails-skill-author` agents
> execute. It does **not** supersede plan 05 — it corrects it; the corrected contract lands in
> `02-schemas-and-contracts.md` (§3.2 / §5.3), which stays the SSOT.
>
> **Process headline (read §9 first):** the feature being fixed *is* the dogfood mechanism. You
> cannot soundly dogfood the scope enforcer using the broken scope enforcer. The remediation is
> **hand-implemented by the agent team on a reconciled branch and independently verified
> first**; re-dogfooding (#92) happens **only after** the corrected harness is proven green by
> tests written against the real executor seam. Dogfood is the *capstone demonstration*, not the
> *verification gate*.

---

## What's being asked

Turn five blocker issues + a WEAK/NIT roll-up into one coherent remediation spec: the sound
enforcement model, the exact SSOT edits, decisions on glob semantics / `workingDirectory` /
branch reconciliation / re-dogfood, a contract-vs-implementation triage of #93, and a process
recommendation for how to remediate without circular self-dogfooding.

**Ambiguity named & narrowed.** Three points the issues leave open, narrowed here so
implementation does not stall:

1. **#88 fix shape — per-task diff domain vs. global serialization.** The trivially "sound"
   fix is to forbid concurrency (serialize all enforced tasks), which deletes the feature. The
   review's leading candidate — scope the diff/revert domain to the acting task's own
   `writeScope` — keeps concurrency. **Narrowing: adopt the scoped-diff model** (§2). Falling
   back to serialize-everything is a non-starter because concurrency is the entire point of the
   plan-05 bet.

2. **#90 — reject vs. normalize `workingDirectory`.** Both are sound. **Narrowing: reject**
   `action.workingDirectory` alongside a non-universal `writeScope` as a validation error (§4),
   because normalization silently re-bases a security primitive and multiplies the surface a
   reviewer must hold in their head — KISS over a feature nobody has asked for.

3. **Re-dogfood timing.** The plan-05 §12 "headline Reality Gate" implies the dogfood *is* the
   proof. **Narrowing: it is not** — it is a demonstration that runs after independent
   verification (§9). This is the most important narrowing in the document.

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

| Concern | Placement |
|---|---|
| #88 scoped diff/revert domain; live-scope detection; revert source-of-truth; `scope-enforcement.log`; honest-halt-on-unrevertable | **harness** (`Guardrails.Core/Execution/WorkspaceScopeEnforcer.cs`, `TaskExecutor.cs`) + **schema** (§5.3 / §3.2) |
| #89 canonical glob matcher (one `IsInScope`/`Overlaps`/enforcer truth table) | **harness** (`WriteScope.cs`) + **tests** |
| #90 reject `workingDirectory` + non-universal `writeScope` | **harness** (`PlanValidator.cs`, new diagnostic) + **schema** (§3 task.json note) |
| Read-after-write `dependsOn` contract | **schema** (§3.2) + **skill** (`plan-breakdown` already emits it; make it contractual) |
| GR2015 redefinition + empty-ancestor skip | **harness** (`PlanValidator.cs`) + **schema** (§3.2) |
| Stale SSOT cross-refs (§3.1.1, `captured/`, `fileHashes`) | **schema** (docs) |
| #91 branch reconciliation (SKILL.md, #70 re-expand, roadmap §11, golden round-trip) | **skill** + **docs** (process, not code) |
| #92 regenerated plan-05 + real-executor integration tests | **skill** (regenerate) + **tests** (the load-bearing deliverable) |
| #93 perf hints (mtime), symlink/junction guard, case-sensitivity, rename handling | **harness** (implementation) |
| `enforcementIgnore` excluding `src/`/`tests/` for perf | **out of scope** — that would defeat enforcement; see §7 triage |

**Not a v2 bet.** Everything here is *correcting v1 work in flight*. The one roadmap touch is
**restoring** the worktree bet's correct retirement (§5) — disjoint-scope was *meant* to retire
worktree-per-task (#54/#57); the branch's stale roadmap never applied that retirement. No new
v2 scope is opened.

---

## Invariants in play

The remediation strengthens five of the six load-bearing invariants and strains none:

1. **Deterministic guardrails over prompt-judges; judges never alone.** The enforcer is pure
   deterministic harness code (snapshot → content-diff → restore). Untouched — but #88 proves
   "deterministic" is not the same as "sound": a deterministic algorithm with the wrong *domain*
   is deterministically wrong. The fix is a domain correction, not a move to judgement.

2. **Harness is the single writer of merged state; children get snapshots, write fragments.**
   The workspace-revert is the *one* sanctioned exception (SSOT §5.3). #88 is a violation of the
   *spirit* of this invariant: under concurrency the harness, acting for task A, writes (deletes)
   files task B legitimately produced — it is no longer a single writer of *A's* domain, it is an
   uncoordinated writer of the *whole* workspace. **The fix re-establishes the invariant by
   bounding each task's write authority to its own scope.** (§2.)

3. **Verdicts come from files, never CLI exit codes.** #93 flags `git checkout --` and
   `File.Delete` throwing out of the executor → the *Scheduler* reads an exception as a **harness
   abort** (whole run dies) rather than a **failed attempt**. That is the file-not-exit-code
   invariant's cousin: an *un-revertable file* is an actionable per-attempt verdict
   (needs-human-able), not a process crash. The fix routes it through `FailedAttempt`, never an
   uncaught throw. (§2.4.)

4. **`02-schemas-and-contracts.md` is the schema SSOT — a contract change lands there in the
   SAME change.** §2/§3/§4 of plan 05 said "diffs the workspace"; that *was the contract bug*.
   This document rewrites §3.2/§5.3 (§6 below) so code and contract agree again. The branch
   already edited the SSOT (§3.2/§5.3 exist there) — but to the *wrong* (whole-workspace) model;
   §6 supplies the corrected text.

5. **Honest halts — nothing marked done unverified; needs-human is a feature.** #92 is an
   honest-halt failure at the *meta* level: a green dogfood run that did not actually exercise
   the feature is "marked done unverified." The remediation makes the un-revertable-file case a
   first-class honest halt (fail the attempt, surface the path) and makes re-dogfood a
   demonstration gated behind real verification.

6. **Plain files, light setup — no databases/daemons/SaaS in v1.** Preserved. The scoped-diff
   model needs no new infrastructure; the live-scope set is in-memory scheduler state.

**The deeper lens (from `design-principles` → ACID/Isolation):** #88 is textbook **Isolation**
failure. Concurrent transactions (task attempts) share one mutable store (the workspace) with no
isolation of their read/write sets. The plan-05 design *claimed* "soundness by construction" from
disjointness, but built the enforcer as a serial-database actor (snapshot the whole store, diff
the whole store) and then ran it concurrently. The fix is to give each transaction a read/write
set bounded to its declared scope — the database equivalent of predicate locking. We cannot use
real isolation (worktrees) — the plan explicitly rejected them — so we get isolation *by
construction* from disjoint scopes, which means the enforcer **must** respect those same scope
boundaries it is enforcing.

---

## Decisions

### 2. #88 — the enforcer diff domain (the core decision)

**Decision: scope the snapshot / diff / revert domain to the acting task's own `writeScope`,
plus a separate, read-only "outside-all-scopes" detection pass. A sibling's disjoint subtree is
never walked, never hashed, never reverted.**

#### 2.1 The model

The whole-workspace model conflates two questions the enforcer must answer, and answering both by
walking the whole tree is what corrupts siblings:

- **Q1 (revert):** "Did *this task* modify anything inside *its own* `writeScope` incorrectly?"
  — No. In-scope writes are *legitimate* and kept. There is nothing to revert *inside* scope.
- **Q2 (the violation that matters):** "Did this task write *outside* any path it is allowed to
  touch?" — That is the only thing to detect and revert.

The bug is that the old enforcer answers Q2 by diffing the *entire* workspace against this task's
scope, so a *sibling's* in-its-own-scope write looks like *this task's* out-of-scope write. The
fix splits the domain:

**Domain A — the task's own scope (∪ its pre-image of in-scope files).** Hashed pre/post *only*
to support feedback and to confirm in-scope writes are kept. Never reverted.

**Domain B — everything the task is NOT allowed to touch.** This is where a violation lives. But
the enforcer must NOT walk a *sibling's* live scope here, or it re-introduces #88. So Domain B is
narrowed to: **paths that are out-of-this-task's-scope AND not in any concurrently-live task's
scope.** A write into a *disjoint sibling's* live scope is, by construction, the sibling's
business — the scheduler already guaranteed the sibling is the only other writer there, and that
write is in-bounds *for the sibling*. A write into a path that is in *nobody's* live scope is the
true violation: a flailing agent escaping every declared boundary.

#### 2.2 The exact detection rule

For a task `T` with scope `S_T`, running concurrently with live tasks `L = {scopes currently
held by the ScopeLock, excluding S_T}`:

> A path `p` written by `T`'s attempt is a **violation** iff:
> 1. `p` is **not** ignored (`enforcementIgnore`), **and**
> 2. `IsInScope(p, S_T)` is **false** (outside T's own scope), **and**
> 3. for every live scope `s ∈ L`: `IsInScope(p, s)` is **false** (inside nobody else's live
>    scope either).

Condition 3 is the #88 fix. Without it, `p` in a sibling's scope is flagged. With it, only a
genuinely-unowned write is flagged. The set `L` is supplied by the scheduler at enforcement time
— see §2.3.

**How `T` detects a write outside *any* declared scope without walking sibling-owned paths.** It
does *not* walk sibling subtrees. The enforcer hashes only two regions:
- the **pre-image and post-image of `S_T`** (Domain A), and
- a **post-image of the residual region** = workspace minus `enforcementIgnore` minus the union
  of all live scopes `(S_T ∪ L)`.

The residual region is "no live task's territory." A new/modified file appearing there is a
violation. Because the residual *excludes* the union of live scopes, a sibling's subtree is
structurally outside the walk — we never enumerate it, so we can never delete from it. This also
fixes the #93 perf cliff for the common case: N disjoint tasks no longer each re-hash the whole
tree; each hashes its own scope plus the (typically small) residual.

**Edge: overlapping scopes.** When `T` and `U` overlap, the ScopeLock *serializes* them — they
are never concurrently live, so `L` never contains `U` while `T` runs. `T`'s residual then
*includes* `U`'s scope, and a stray `T` write there is correctly a violation (U is not live to
excuse it). Serialization makes the union-of-live-scopes exactly the set of legitimately-busy
regions at any instant. The model is sound at every parallelism level, including 1.

#### 2.3 The seam — the scheduler must hand the enforcer the live-scope set

`TaskExecutor` cannot compute `L` alone (it executes one task; it does not know what else is
live). The **`ScopeLock` is the authority on "what scopes are held right now"** — it already
tracks `_held`. The contract:

- `ScopeLock` exposes a snapshot of currently-held scopes (excluding the caller's own), captured
  **at enforcement time** (post-action, immediately before the residual walk), under its gate.
- The scheduler passes this `IReadOnlyList<WriteScope> liveScopes` down through `ITaskExecutor`
  into `WorkspaceScopeEnforcer.RevertOutOfScope(...)`.
- The enforcer computes the residual region = workspace − `enforcementIgnore` − `(S_T ∪
  liveScopes)`, walks **only** that region + `S_T`, and applies §2.2.

**Why "at enforcement time," not "at acquire time":** a sibling may finish and release between
`T`'s action and `T`'s revert. Capturing `L` late (just before the walk) is conservative in the
right direction: a *just-released* sibling's region rejoins the residual, so if `T` wrote there it
is now flagged (correct — nobody owns it anymore) and the just-finished sibling already reverted
its *own* out-of-scope damage before releasing. A sibling that is *still live* is excluded, so its
in-flight writes are never touched. There is no instant at which `T`'s revert can delete a *live*
sibling's in-scope write. (This is the property #88's test must pin — §8.)

**Devil's-advocate check on §2.3 — the release race.** *Counter:* "between `T` capturing `L` and
`T` finishing its residual walk, sibling `U` could release AND a *third* task `V` (overlapping
U's freed region) could acquire and write there — `T`'s stale `L` would now flag V's legitimate
write." *Response:* No. `T`'s residual walk only flags paths *`T` itself* wrote (every violation
is computed against `T`'s own pre/post image — a path `T` never touched is not in `T`'s diff at
all). `T` cannot "flag V's write" because `T` only ever inspects/reverts files whose content
changed *between T's pre-image snapshot and T's post-image* — i.e. files in `T`'s causal cone. The
only way a path is both "changed in T's window" and "in V's scope" is if `T` and `V` ran
concurrently with overlapping scopes — which the ScopeLock forbids. So the residual model reverts
only `T`'s own escapes, never a third party's writes. `L` is used purely to *suppress* flagging
T's writes that landed in a live sibling's scope; a stale `L` can only *over-flag T's own out of
scope writes* (fail-safe: T failing its own attempt), never touch another task's file.

**Devil's-advocate check #2 — does `L` even need to exist?** *Counter:* "If `T`'s revert only
ever touches files in `T`'s causal cone, and a disjoint sibling never writes into `T`'s cone,
then `T` walking the whole workspace but only reverting *its own diff* is already safe — `L` is
redundant." *Response:* This is the sharpest objection and it is **half right**, and it changes
the implementation. The corruption in #88 is `T` reverting a path it did *not* write — but the old
code's diff is `current vs T's-pre-image`, and a sibling's file *is* "new since T's pre-image,"
so it *does* enter T's diff as a phantom "new file." The phantom arises because `T`'s pre-image
was taken over the *whole tree* (so a sibling file absent at T-start, present at T-revert, reads
as "T created this"). **Two valid fixes follow, and we choose both layers for defense in depth:**
(a) **bound T's pre/post image to `S_T ∪ residual`** so a live sibling's file is never in T's
snapshot to begin with (the §2.1 domain split — this alone fixes #88); and (b) keep `L` as the
*detection-rule* refinement (§2.2 condition 3) so that even within the residual, a path that turns
out to be in a live scope is excused. Layer (a) is load-bearing; layer (b) is the belt to (a)'s
braces and is what makes the *contract* (§6) state the rule cleanly. We ship both. `L` is **not**
redundant for the contract even if layer (a) alone fixes the test, because the residual is
computed *from* `L` — they are the same set, named once.

#### 2.4 Revert source-of-truth (resolves #93: `git checkout --` restores COMMITTED bytes)

**Decision: the byte baseline is the source of truth for *all* out-of-scope reverts. `git
checkout --` is removed from the revert path entirely.**

The review proved `git checkout -- <path>` restores the **last committed** bytes, not the
**pre-attempt** bytes. If an *earlier* task legitimately produced uncommitted work in a region
`T` later strays into, `git checkout --` blows that earlier work away — corrupting a *different*
sibling's output a second way, and contradicting plan-05 §5.3's "exact pre-attempt bytes" claim.

The fix:
- **Every file in the residual region gets a lazy byte snapshot into `state/scope-baseline/`** at
  `T`'s pre-action snapshot (not just untracked files — *all* of them). Revert restores from this
  baseline. This is the only source that holds *pre-attempt* bytes by construction.
- **Created out-of-scope file → delete** (unchanged; correct).
- **Modified/deleted out-of-scope file → restore from `state/scope-baseline/`** (was: git first,
  baseline fallback → now: baseline only).
- `git checkout --`, `TryRestoreFromGit`, and `NormalizeGitObjectPermissions` are **deleted**.
  This also removes the #88 "compounding" smell (concurrent `git` on a shared `.git`, whole-`.git`
  permission walk per revert) and the symlink-escape vector through `.git`.

Because the residual region is small (it excludes every live scope and `enforcementIgnore`), the
"snapshot all residual bytes" cost is bounded — far cheaper than the old whole-tree
`SaveOutOfScopeBytesToBaseline`, which copied the entire out-of-scope tree every attempt and
could *launder a silently-failed prior revert into the baseline* (#93). The new baseline is taken
from the residual only, before the action, so it always reflects true pre-attempt bytes.

#### 2.5 `scope-enforcement.log` + honest-halt on un-revertable files (resolves #93)

Plan-05 §5.3 *promised* a `scope-enforcement.log` and "loud-on-failure" audit; the code never
wrote one, and worse, `RevertOutOfScope` has **no error handling** — a locked/un-deletable file
throws out of the executor and the Scheduler treats it as a **harness abort** (whole run dies).
That violates honest-halt (invariant 5) and the file-not-crash principle (invariant 3).

**Decision:**
- `RevertOutOfScope` wraps every `File.Delete` / `File.Copy` in try/catch. Each revert
  *attempt* — success or failure — appends a line to `state/logs/<task>/attempt-N/scope-enforcement.log`:
  `<action> <relPath> <result>` (`delete|restore`, `ok|FAILED: <reason>`).
- **An un-revertable file fails the *attempt*, not the *run*.** The attempt's outcome is
  `action-failed` with feedback naming the un-revertable path(s) and the audit-log location. The
  Scheduler's existing block-the-transitive-closure path then applies — needs-human after the
  budget, dependents blocked, independent branches keep running. **No exception escapes the
  executor.**
- The reason text distinguishes "reverted N out-of-scope writes" (clean fail) from "could NOT
  revert M out-of-scope writes — workspace may be dirty; see scope-enforcement.log" (loud fail).

This is the #51 FIX-D precedent applied correctly: the harness never silently leaves corruption,
and never crashes the whole run over one locked file.

### 3. #89 — canonical glob semantics (one matcher, two callers)

**Decision: `WriteScope` owns one canonical segment-matcher with literal-prefix correctness;
`Overlaps` (scheduler) and `IsInScope` (enforcer) both derive from it; `WorkspaceScopeEnforcer.MatchGlob`
is deleted and the enforcer calls `WriteScope.IsInScope`.**

The proven bug: `MatchPath` (the *membership* test driving revert) discards the literal prefix on
any `*`-bearing segment — `if (seg.Contains('*')) return MatchPath(path, si+1, pattern, pi+1);`
(`WriteScope.cs:163`). So `IsInScope` *under-detects* (permissive), while `Overlaps` *over-detects*
(conservative) — two contradictory glob semantics keying scheduling vs. enforcement. "Soundness by
construction" is false when the construction uses two different definitions of "in scope."

**The truth table the implementation + tests MUST satisfy** (`*` matches within one segment
honoring its literal prefix/suffix; `**` spans any depth; comparison is `OrdinalIgnoreCase` on
Windows, `Ordinal` elsewhere — the existing `SegmentComparison`):

| # | Scope glob | Path | `IsInScope` | Why |
|---|---|---|---|---|
| 1 | `src/Feat*/**` | `src/OtherDir/Z.cs` | **false** | literal prefix `Feat` must match the segment start — the proven `:163` bug returns true today |
| 2 | `marks/left*` | `marks/right.start` | **false** | `left` prefix; the #89 divergence-with-`Overlaps` case |
| 3 | `marks/left*` | `marks/left.start` | **true** | prefix matches |
| 4 | `src/**/*.cs` | `src/x/secrets.json` | **false** | extension glob: the final `*.cs` segment must end in `.cs`; today returns true (extension never checked) |
| 5 | `src/**/*.cs` | `src/x/y/Thing.cs` | **true** | `**` spans `x/y`, final segment matches `*.cs` |
| 6 | `src/**/*.cs` | `Foo.txt` | **false** | not under `src/`, wrong extension |
| 7 | `src/Feature/**` | `src/FeatureX/Z.cs` | **false** | sibling-prefix trap — `FeatureX ≠ Feature` as a whole segment (`**` does not start a new segment match mid-segment) |
| 8 | `src/Feature/**` | `src/Feature/a/b.cs` | **true** | `**` spans `a` |
| 9 | `src/Feature` (bare) | `src/Feature/x.cs` | **true** | bare dir normalizes to `src/Feature/**` |
| 10 | `src/Feature` (bare) | `src/Feature` | **false** | a directory scope matches files *under* it, not the dir entry itself (no files at a dir path) |
| 11 | `[]` (empty) | *anything* | **false** | empty scope owns nothing |
| 12 | `["**"]` (universal) | *anything* | **true** | universal owns everything |
| 13 | `src/A/*` | `src/A/B/c.cs` | **false** | single `*` is one level only |
| 14 | `src/A/*` | `src/A/b.cs` | **true** | one level matches |
| 15 | `src/*/Tests/**` | `src/Foo/Tests/X.cs` | **true** | `*` matches `Foo`, `**` spans the rest |
| 16 | `*.md` | `README.md` | **true** | top-level extension glob |
| 17 | `*.md` | `docs/README.md` | **false** | `*.md` is one segment; not under `docs/` |

**Consistency law (must be a test):** for any non-empty `S` and path `p`, if `IsInScope(p, S)`
then `Overlaps(S, Parse([p-as-literal-glob]))` — membership implies overlap. The #89 contradiction
(`Overlaps(['marks/left*'],['marks/right*']) = false` while `IsInScope('marks/right.start',
['marks/left*']) = true`) must become impossible. Implement `IsInScope` and the membership half of
`Overlaps` from the **same** segment-match helper so they cannot drift again (DRY on a security
primitive).

**Also (folds in from #93 tests bucket):** add the missing `IsInScope` unit suite (there is none
today), and replace the tautological `_ = Parse(...)`-with-no-assertion parse tests.

### 4. #90 — `workingDirectory` base

**Decision: reject `action.workingDirectory` when the task declares a non-universal `writeScope`
(an explicit `[]` or any narrow scope). New validation error.**

The enforcer roots its snapshot/revert at `_plan.Workspace` and interprets globs
workspace-relative, but the action runs in `ResolveWorkingDirectory(task)` which is a *different*
directory when `workingDirectory` is set. So declared scope and classified paths don't correspond:
a subdir working-dir hides out-of-scope writes; an elsewhere working-dir reverts legitimate ones.

**Why reject, not normalize:**
- **Soundness primitive, not a convenience.** `writeScope` enforcement is the test-protection
  mechanism (the triad is gone). Re-basing observed paths through `workingDirectory` adds a
  translation layer to a security check — exactly where a subtle bug means "out-of-scope write
  slips through green." KISS says do not put a transform in the trust path you don't need.
- **YAGNI.** No current plan combines a custom `workingDirectory` with a narrow `writeScope`; the
  branch's own plan-05 uses neither together. We are not removing `workingDirectory` (a universal
  or absent scope still allows it — a repo-wide task that cd's elsewhere is fine); we forbid only
  the *ambiguous combination*.
- **Honest and loud.** A validation error at `validate`/CI time is the honest-halt: "these two
  fields can't both be set; pick one." Normalization would silently *appear* to work while the
  threat model quietly shifts.

**New diagnostic — `GR2018` (error):** "Task '<id>' sets `action.workingDirectory` together with a
non-universal `writeScope`. Scope enforcement is workspace-relative; a custom working directory
makes the declared scope and the enforced paths diverge. Remove `workingDirectory`, or declare a
universal `["**"]`/absent scope (which disables disjoint-scope enforcement for this task)." (A
universal scope reverts nothing out-of-scope by definition, so the base mismatch is harmless
there.)

### 5. #91 — branch reconciliation

The branch's merge-base (`52a6561`) predates **#64** (entry-point-wiring + live-smoke-test),
**#66** (UI-presence), **#70** (multi-line `if`-block standardization), **#80** (OSC 8 clickable
diagram link), and the **roadmap split** (#54/#57) + **E2E bet #5** (#78). Shipping the branch tip
silently reverts all of them. The large skill "deletions" in the two-dot `master..branch` diff are
an **artifact of staleness** — they are master's *additions the branch never had*; the three-dot
`master...branch` diff is the real, much smaller change set. The Explore pass confirmed the
collisions are **orthogonal sections**, except #70 which the branch actively regressed.

**Decision: rebase/merge current master into the branch and resolve deliberately, by rule.**

1. **`plan-breakdown/SKILL.md` — keep BOTH doctrines.** They touch different Steps:
   - **From master, keep verbatim:** Step 4 decision checks "Executable entry-point wiring"
     (#64) and "UI-facing deliverable" (#66); Step 5 insertion rules "Server/executable plan →
     wire-entrypoint + live smoke-test" and "UI-facing plan → UI-implementation task +
     UI-presence guardrails."
   - **From branch, keep:** Step 5 "writeScope for the TDD pair" subsection and Step 6 "writeScope
     — every task declares one."
   - **Resolution rule (the doctrine that resolves it):** *"what to verify" (master's #64/#66
     output-analysis doctrine) and "who may write" (the branch's ownership doctrine) are
     orthogonal axes of task specification — every task gets both. A merge that drops either is
     wrong."* This sentence goes in the reconciliation commit message and the skill author's
     handoff.
   - **References that auto-merge to master (keep master's):** `references/stacks/dotnet.md` §7
     (entry-point wiring), §8 (live smoke-test), §9 (UI-presence) — the branch dropped these
     because it predated them; restore them. They are the *realizations* of the #64/#66 doctrine;
     keeping them while dropping the SKILL.md doctrine (or vice-versa) is the incoherent
     half-state #91 warns about.

2. **Re-expand #70's multi-line `if`-block form, and restore the rule.** The branch deleted the
   SKILL.md Step 4 rule and reintroduced the banned single-line form
   `if (...) { Write-Output "..."; exit 1 }` in:
   - `references/example-breakdown.md:120,166,189,190,207`
   - `references/stacks/dotnet.md:107,115`
   Restore the Step 4 rule verbatim from master and re-expand every collapsed block in the
   branch's examples to the multi-line form. **Additionally:** every guardrail script the
   *regenerated plan-05* (#92, §6 below) emits must use the multi-line form — the golden example
   must not demonstrate the pattern the SKILL forbids.

3. **Roadmap §11 — restore the correct retirement (refines #91 step 3).** The branch's roadmap is
   **stale, not merely "inverted"**: it still carries the *pre-split* worktree bet #1 full text,
   risk #2 "worktrees are the v2 fix," and has **no** E2E bet #5 — because it predates #54/#57 and
   #78. The reconciliation must produce master's current roadmap **plus** plan-05's intended §11
   retirement *applied on top*:
   - **v2 bet #1 (worktree-per-task):** retire/annotate — point at plan 05 + this plan 06 as the
     v1.x disjoint-scope replacement (no git, no merge). Keep the #54/#57 issue references as
     "superseded by disjoint-scope" rather than deleting them.
   - **Risk register #2 ("parallel tasks sharing one workspace"):** rewrite from "worktrees are
     the v2 fix" to "enforced disjoint `writeScope` (plan 05/06) — scoped diff/revert per task."
   - **Drop the `exclusive` mention** (the field is removed from the schema).
   - **Restore E2E web-UI bet #5 (#78)** — the branch deletes it; it must survive.

4. **Re-run the golden round-trip** (`guardrails validate examples/hello-guardrails/hello-guardrails`)
   after the merge, and re-prove the `plan-breakdown` round-trip emits both doctrines clean.

### 6. Contract (SSOT) changes — exact §3.2 / §5.3 rewrites

These are the verbatim edits `02-schemas-and-contracts.md` requires. The branch already wrote
§3.2/§5.3 to the *whole-workspace* model; these **replace** that text with the scoped-diff model.
The harness-dev and test-author execute against this section.

#### 6.1 §3.2 `writeScope` — replace the "Enforcement" paragraph

Replace the current §3.2 bullet 2 ("Enforcement. Before the action starts, the harness snapshots
the workspace…") with:

> 2. **Enforcement (scoped, concurrency-sound).** Before the action starts, the harness snapshots
>    **only** the bytes of (a) the task's own `writeScope` and (b) the *residual region* — the
>    workspace minus `enforcementIgnore` minus the union of all **concurrently-live** task scopes.
>    It never walks or hashes another live task's subtree. After the action succeeds and before
>    guardrails run, it re-diffs those same two regions. A path is an **out-of-scope write** iff it
>    changed in this attempt, is outside the task's own `writeScope`, **and** is inside no
>    concurrently-live task's scope. Such writes are **reverted** (created files deleted;
>    modified/deleted files restored from `state/scope-baseline/`, which holds the *pre-attempt*
>    bytes of the residual region) and the attempt **fails** with feedback naming the violating
>    paths. A path that lands inside a disjoint sibling's live scope is **not** the acting task's
>    violation — the scheduler guarantees that sibling is the sole other writer there — so it is
>    neither walked nor reverted by this task.

Add a new paragraph after the Glob format note:

> **Read-after-write contract.** Write-scope protects *writers*; the DAG protects *readers*. A task
> whose action **reads** a file another task **produces MUST declare `dependsOn` that producer.**
> Disjoint scopes make two tasks concurrent; if one then reads the other's half-written output the
> result is undefined. `plan-breakdown` emits this edge; `guardrails-review` flags a narrow-scoped
> task that references another independent task's outputs with no dependency edge. (This contract
> was specified in plan-05 §8 and never landed here — it is load-bearing for read correctness under
> concurrency.)

Rewrite the **GR2015** bullet (resolves #93 "overlap vs excludes-ancestor-outputs" + empty-skip):

> - **`GR2015` (error):** a task that depends (directly or transitively) on an ancestor with a
>   **non-empty** declared `writeScope` must **exclude that ancestor's outputs** from its own
>   effective scope — i.e. the dependent's scope must NOT contain any path the ancestor owns. This
>   protects an ancestor's products (notably authored tests) from a dependent's revert in a retry.
>   The check is *containment of the ancestor's scope within the dependent's*, not mere "overlap":
>   a dependent legitimately writes a **disjoint** sibling region of the same parent directory.
>   Ancestors with an **empty `[]`** scope (gate/state-only — they own no files) are **skipped**
>   (there is nothing to protect). An **absent/universal** dependent scope that swallows a
>   protected ancestor is the precise "tests left unprotected" case GR2015 exists for and **is** an
>   error.

> [Note for the harness-dev: today `ValidateWriteScopeSubsumption` uses `WriteScope.Overlaps`,
> which is symmetric and conservative — it fires on a disjoint sibling-subtree dependent and
> *misses* the empty-ancestor case it should skip. Replace the predicate with directional
> *containment* ("does the dependent's scope include any path in the ancestor's scope?") and add
> the `ancestor.Scope.IsEmpty → continue` skip. `PlanValidator.cs:326-355`, `:339`.]

#### 6.2 §5.3 — replace "exactly one case" body with the scoped, baseline-only, audited revert

Replace the §5.3 indented contract block with:

> **The harness writes a workspace file only when reverting an *out-of-scope* write at the end of a
> failed attempt — and never otherwise.** "Out-of-scope" is defined in §3.2: a path the acting task
> wrote that is outside its own `writeScope` *and* inside no concurrently-live task's scope. Each
> revert write targets a path that (a) is out-of-scope by that definition, (b) resolves against the
> plan workspace, and (c) passes the workspace-containment check immediately before the write.
> Modified/deleted out-of-scope files are restored from `state/scope-baseline/`, which captures the
> **exact pre-attempt bytes** of the residual region at the attempt's pre-action snapshot
> (`git checkout --` is **not** used — it would restore committed, not pre-attempt, bytes and could
> destroy an earlier task's uncommitted work). Created out-of-scope files are deleted. Every revert
> — and every file the harness **could not** revert — is appended to
> `state/logs/<task>/attempt-N/scope-enforcement.log`. **An un-revertable file fails the *attempt*
> (reason names the path + the log), never aborts the *run*:** the executor catches all
> revert I/O errors and routes them through the normal failed-attempt path, so the scheduler blocks
> the transitive closure and surfaces needs-human while independent branches finish.

Update the §8 per-attempt log-layout list to include `scope-enforcement.log`.

#### 6.3 Stale cross-references to fix (resolves #93 docs bucket)

- **`§3.1.1` references** (now-deleted captureHashes/restoreOnRetry sections): every cross-ref to
  `§3.1.1`, `state/captured/`, and `fileHashes` in the §6.1 `--fresh` list, §6.3 conflict-row
  example, §7 reset note, and §6.2 poisoning example must be repointed:
  - §6.1 `--fresh` deletion list: replace "`captured/` baseline store (§3.1.1)" with
    "`scope-baseline/` store (§3.2)".
  - §7 `succeeded`/reset note: replace "clears that task's captured baseline store
    (`state/captured/<task-id>`, §3.1.1)" with "clears that task's scope baseline
    (`state/scope-baseline/<task-id>`, §3.2)".
  - §6.2/§6.3 `fileHashes` examples: re-key the illustrative conflict path off a neutral example
    (e.g. `01-author.<somekey>`) — `fileHashes` was a captureHashes artifact and no longer exists.
- §5.3 "failed attempt" phrasing vs §3.2 "after the action succeeds": align — enforcement runs
  **after the action succeeds, before guardrails**; a violation **makes the attempt fail**. Use
  that exact ordering in both sections.

#### 6.4 New diagnostic registered (§3 task.json note + DiagnosticCodes)

- Add **GR2018** (the #90 `workingDirectory` + non-universal `writeScope` conflict, §4) to the §3
  `task.json` annotated comment block and to `Loading/DiagnosticCodes.cs`.

#### 6.5 §6.1 state-reset fix (resolves #93 state-reset)

`RunReset.Task` currently clears `state/captured/<id>` (a dead store) but **not**
`state/scope-baseline/<id>`. The §7 reset note edit above is the contract; the implementation note:
`RunReset.cs:64` must delete `state/scope-baseline/<id>` (and stop referencing `captured/`).
`--fresh` already wipes the whole tree; only single-task reset is buggy.

### 7. #93 triage — contract-level vs pure-implementation

Each WEAK/NIT, tagged. **CONTRACT** items fold into the §6 SSOT spec above and are part of this
design's contract surface; **IMPL** items hand directly to `guardrails-harness-developer` /
`guardrails-test-author` with no further architectural decision.

| # | Finding | Tag | Where it lands |
|---|---|---|---|
| 1 | No `scope-enforcement.log`; un-revertable aborts whole run | **CONTRACT** | §2.5 + §6.2 |
| 2 | `git checkout --` restores committed not pre-attempt bytes | **CONTRACT** | §2.4 + §6.2 |
| 3 | Out-of-workspace writes undetected; symlinks/junctions followed | **CONTRACT (detection) + IMPL** | Contract: detection must cover residual region & containment-check on *detection* not just revert (§2.2). Impl: `Directory.EnumerateFiles` must not follow reparse points — use a walk that skips symlink/junction dirs; containment-check each path on detection. |
| 4 | Case-sensitivity: snapshot keys `Ordinal`, `IsInScope` `OrdinalIgnoreCase` on Windows → phantom delete+create | **IMPL** | Key the hash dictionaries with the same `SegmentComparison`/path comparer `IsInScope` uses, so a case-only rename isn't a phantom. (No contract change — the contract says "content-based"; this is making the keying match.) |
| 5 | Rename / nested-dir mutation kinds untested | **IMPL (test)** | §8 test list — cross-scope rename = delete-old + create-new; pin net behavior. |
| 6 | mtime/size skip-hint promised, not implemented; full-tree SHA-256 ×2/attempt; LOH `ReadAllBytes` | **IMPL** | The §2 scoped-diff already removes the *whole-tree* ×N cost. mtime/size skip-hint within the (now smaller) regions is an IMPL optimization, **not** a violation signal (SSOT §5.3 keeps content-based detection). Stream-hash to avoid LOH. |
| 7 | `SaveOutOfScopeBytesToBaseline` copies whole out-of-scope tree; can launder a failed revert | **CONTRACT** | §2.4 — baseline taken from *residual only*, pre-action; cannot launder. |
| 8 | `writeScope: []` is a silent no-op gate; no validator checks the task produces output | **IMPL (skill) — DEFER** | Out of scope for the soundness fix. A `[]` gate that writes nothing IS correct behavior (gate tasks exist). "Did it produce its claimed output?" is a *guardrail-strength* question `guardrails-review` already owns. No new validator. |
| 9 | Over-broad/universal dodges enforcement; GR2015 fires on edge only & skips empty/absent ancestor; "overlap" vs "excludes ancestor outputs" | **CONTRACT** | §6.1 GR2015 rewrite (containment, empty-skip). The "universal dodges enforcement with no hard stop" is *by design* (universal = opt out, §4 GR2018 note) — `guardrails-review` challenges it, not a hard validator error. |
| 10 | Read-after-write contract never landed in SSOT | **CONTRACT** | §6.1 read-after-write paragraph. |
| 11 | Stale SSOT cross-refs (`§3.1.1`, `captured/`, `fileHashes`, `§6.1 --fresh`) | **CONTRACT** | §6.3. |
| 12 | Roadmap §11 inverted; `exclusive` referenced; E2E bet #5 deleted | **CONTRACT (docs)** | §5 step 3 (#91). |
| 13 | Stale skill notes (`exclusive` field, "until M7") | **IMPL (skill)** | Folds into #91 reconciliation. |
| 14 | `RunReset.Task` clears `captured/` not `scope-baseline/` | **CONTRACT + IMPL** | §6.5. |
| 15 | Tautological parse tests (`_ = Parse(...)`) | **IMPL (test)** | §8. |
| 16 | `IsInScope` has no direct unit test | **IMPL (test)** | §3 truth table + §8. |
| 17 | `ScopeLock.Release` value-equality on override-less struct (`WriteScope` no `Equals`) | **IMPL** | `_held.Remove(scope)` works only because production reuses the instance. Give `WriteScope` value equality OR have `ScopeLock` hold an identity token (a `Guid`/handle returned from `Acquire`). Prefer the **handle** — it also cleanly carries "this hold's scope" for the §2.3 live-scope snapshot. |
| 18 | `ParallelRunTests` uses `Start-Sleep`/timing not gates | **IMPL (test)** | §8 — block-until-release gates, not wall-clock. |
| 19 | `ScopeRevertTests` shells real `git` untraited | **IMPL (test)** | Moot once §2.4 deletes `git` from the revert path; any residual git test gets an opt-in trait. |
| 20 | `MaxParallelism_Caps` asserts `<= 2` not `== 2` | **IMPL (test)** | §8 — a strictly-serial scheduler must fail this test. |
| 21 | `02-enforcer-wired-into-executor.ps1` is a keyword-grep (passes a non-invoking seam) | **IMPL (skill/dogfood)** | §8 — the regenerated plan-05 enforcer-wiring guardrail must be *structural/behavioral* (a test that the executor actually invokes the enforcer on a real attempt), not a `grep WorkspaceScopeEnforcer`. |
| 22 | Broken `<see cref="WorkspaceLock"/>` doc refs; stale comments; dead 3-arg `Snapshot` overload | **IMPL (NIT)** | Housekeeping in the harness change. |

---

## Devil's-advocate self-critique

Run against my own core decision (§2). The four strongest counter-arguments and my responses:

1. **"You are reinventing predicate locking / a transaction manager in a tool whose whole pitch is
   'plain files, light setup.' The residual-region + live-scope-set machinery is more complex than
   the bug it fixes. The honest fix is: serialize all enforced tasks (parallelism only for
   universal/`[]` gate tasks), ship that, and revisit concurrency in v2 with real isolation
   (worktrees)."**
   *Response — partly conceded, and it sharpens the recommendation.* This is the most serious
   objection because it questions whether the *feature* is worth its soundness cost. But
   serialize-everything deletes the headline value (independent narrow-scope tasks running
   concurrently is the entire plan-05 bet), and worktrees were rejected *with a four-problem
   review* (plan-05 §1). The residual model is not a transaction manager — it is one set
   subtraction (`workspace − ignore − live-scopes`) computed at one point per attempt; the
   ScopeLock already tracks `_held`. The added surface is: ScopeLock exposes its held set, the
   enforcer subtracts it. That is genuinely small. **Where the objection wins:** it is why §9
   insists the corrected harness is verified by *direct tests against the real executor seam*
   before any dogfood — if the residual model proves fiddly in practice, we find out from a unit
   test, not from a corrupted dogfood run. And it is why §2.3's devil's-advocate-#2 keeps the
   *load-bearing* fix as the simple domain-split (layer a), with `L` as the named contract layer
   — if layer (b) ever feels like over-engineering, layer (a) alone still closes #88.

2. **"The live-scope set is captured 'at enforcement time under the ScopeLock's gate.' You are now
   holding a lock across an I/O-bound residual walk, or you snapshot-then-release and the set is
   immediately stale. Either a contention bottleneck or a TOCTOU race — pick your poison."**
   *Response.* Snapshot-then-release: capture the held-set under the gate (O(holders), in-memory,
   microseconds), release the gate, then walk. The staleness is *fail-safe* (devil's-advocate
   check in §2.3): a stale `L` can only cause `T` to over-flag *its own* out-of-scope write
   (failing T's attempt — correct-ish, never corrupting), never to touch another task's file,
   because T only inspects files in its own causal diff. No lock is held across I/O; no TOCTOU on
   another task's data.

3. **"Baseline-snapshot the *entire residual region* every attempt to get pre-attempt bytes? On a
   real repo the residual is 'the whole repo minus a couple of src subtrees' — you have just moved
   the whole-tree copy from `SaveOutOfScopeBytesToBaseline` into the new design, the very perf
   cliff #93 flagged."**
   *Response — the sharpest perf objection.* Conceded that a *naive* residual baseline is large.
   Mitigation, in order: (a) the baseline is **lazy/content-addressed** — only files that
   *actually change* in the attempt need their pre-image, and we only need pre-image bytes for a
   file *if it turns out out-of-scope*; capture the pre-image **on first detected change** (mtime
   hint to skip unchanged files, then hash, then — only for a changed out-of-scope file — copy its
   pre-image, which we still hold because the change was detected against the pre-image hash). The
   honest version: snapshot **hashes** of the residual (cheap), and lazily snapshot **bytes** only
   for residual files that change. A flailing agent writes a handful of stray files, not thousands
   — the byte-copy set is the violation set, which is tiny in the common case. This is an IMPL
   refinement (#93 item 6/7) but it must be in the harness-dev handoff, so I am stating it here as
   a constraint: **byte baseline is lazy and bounded by the violation set, not the residual size.**

4. **"`enforcementIgnore` defaults exclude `bin/obj/node_modules/.git/state`. A flailing agent that
   writes a malicious file *inside* `node_modules/` or `.git/hooks/` escapes enforcement entirely —
   and you just made `git`-based restore go away, so there is no second line of defense."**
   *Response — accepted as a known, documented limit, not fixed here.* `enforcementIgnore` is a
   deliberate trust boundary (a build guardrail legitimately writes `bin/`); it has always been an
   escape hatch and plan-05 §5.2 documented it. Narrowing it (e.g. never ignoring `.git/hooks`) is
   a reasonable hardening but is **out of scope** for the soundness remediation and belongs in the
   `guardrails-review` "challenge a broad ignore set" doctrine or a follow-up. I flag it so it is a
   *named* residual risk, not a silent gap — consistent with honest-halt.

**Net:** the design survives self-critique with two binding refinements pulled forward into the
handoff — (i) the byte baseline must be lazy/violation-bounded (counter 3), and (ii) the
load-bearing fix is the domain-split with `L` as the contract layer (counter 1). The
serialize-everything alternative is rejected on value grounds but its discipline is honored by §9.

---

## 9. Process recommendation (the critical strategic call)

**You cannot soundly dogfood the scope enforcer using the broken scope enforcer.** Plan-05's §12
made the dogfood *the* Reality Gate; #92 proved that gate was never met (the dogfood ran under
`preview.19`, a pre-`writeScope` harness — it proved the *old* harness still works, not that the
new feature is sound). Re-running a dogfood on a *still-broken* enforcer would, at best, corrupt
its own concurrent tasks (#88) and, at worst, *appear green* by accident at parallelism 1.

**Recommendation: hand-implement on a reconciled branch, verify independently, then re-dogfood as a
capstone demonstration — in this staged order.**

- **Stage 0 — Reconcile (no feature code).** Apply #91 (§5): merge master into the branch, keep
  both doctrines, re-expand #70, restore roadmap §11 + bet #5, re-prove the golden round-trip. A
  clean, current base before any harness change. Owner: `guardrails-skill-author` (skills/docs) +
  the lead for the merge.

- **Stage 1 — Land the corrected contract (this doc → SSOT).** Apply §6 to
  `02-schemas-and-contracts.md` in the same change that the harness work begins. Contract-first so
  code and SSOT never diverge (invariant 4). Owner: `guardrails-architect` (me) proposes the exact
  text; lead applies.

- **Stage 2 — Hand-implement the harness fix, agent-team, test-first.** `guardrails-test-author`
  writes the real-executor integration suite (§8) *first* — including the #88
  concurrent-disjoint-no-corruption test that the green plan-05 run never had — proving they FAIL
  against the current (broken) enforcer. Then `guardrails-harness-developer` implements §2/§3/§4 to
  green. Build+test is the hard gate. **This is hand-implementation, not a dogfood** — precisely
  because the mechanism under repair cannot be trusted to repair itself.

- **Stage 3 — Independent verification.** The full suite green on the 3-OS CI, with the §8 tests as
  the load-bearing evidence (not a wall-clock parallel test). `guardrails-devils-advocate` does a
  "cheapest wrong enforcer that passes these tests" pass. Only when Stage 3 is green is the harness
  *trusted*.

- **Stage 4 — Re-dogfood as a CAPSTONE, on the verified harness (#92).** Regenerate plan-05 under
  the now-`writeScope`-honoring harness (writeScope on every task, no triad, `enforcementIgnore`
  set, no triad guardrails) and run it — *from the global tool / `dotnet run` Debug build, never
  the Release self-lock* (per the dogfood-safety memory). The dogfood now *demonstrates* the
  feature on itself; it is no longer the thing that *proves* it. If the dogfood reveals a gap, that
  gap is a missing §8 test — add it at Stage 2 and re-verify, never "fix it live in the dogfood."

**Why staged, not "just re-dogfood after a quick patch":** the dogfood's value is *demonstration of
a verified capability*, and its risk is *circular trust* (a broken enforcer certifying itself). The
staging severs the circularity: tests written against the real executor seam are the trust anchor;
the dogfood is the victory lap. This also matches the project's own doctrine — deterministic
verification over self-report, honest halts over green-by-accident.

This is substantial multi-finding remediation, so per the standing instruction it goes to the
**agent team** (harness-dev + test-author + skill-author), not solo — with hands-on lead review
before completion of each delegated stage.

---

## 8. Implementation handoff (agent + filesTouched + sequencing)

Sequenced; later stages depend on earlier. Each task names its implementing agent and the files it
touches.

### Stage 0 — Reconciliation (`guardrails-skill-author` + lead)
- filesTouched: `.claude/skills/plan-breakdown/SKILL.md`,
  `.claude/skills/plan-breakdown/references/example-breakdown.md`,
  `.claude/skills/plan-breakdown/references/stacks/dotnet.md`,
  `.claude/skills/plan-breakdown/references/guardrail-catalogue.md`,
  `.claude/skills/plan-breakdown/references/schemas.md`,
  `.claude/skills/guardrails-review/SKILL.md`,
  `.claude/skills/guardrails-domain-knowledge/SKILL.md`, `docs/plans/03-roadmap.md`.
- Done when: master merged in; both doctrines present; no single-line `if` in examples; roadmap has
  bet #1 retired-to-plan-05/06, risk #2 rewritten, bet #5 present; golden round-trip clean.

### Stage 1 — SSOT contract (`guardrails-architect` proposes, lead applies)
- filesTouched: `docs/plans/02-schemas-and-contracts.md` (§3.2, §5.3, §6.1, §6.3, §7 note, §8 log
  list, §3 task.json GR2018 comment), `docs/plans/06-scope-enforcement-remediation.md` (this doc,
  already written).
- Done when: §6 edits applied verbatim; no `§3.1.1`/`captured/`/`fileHashes` cross-refs remain.

### Stage 2 — Harness fix, test-first (`guardrails-test-author` then `guardrails-harness-developer`)
- **Tests first** (`guardrails-test-author`), filesTouched under `tests/`:
  - `WriteScopeTests.cs` — the §3 truth table (17 rows) for `IsInScope`; the membership-implies-overlap
    consistency law; replace tautological parse tests.
  - **New real-executor integration suite** (`tests/Guardrails.Integration.Tests/`):
    - **concurrent-disjoint-no-corruption (#88):** two tasks A(`src/Alpha/**`) / B(`src/Beta/**`)
      run concurrently through the **real `TaskExecutor`**; B writes its in-scope file while A is
      between snapshot and revert; assert B's file survives AND A succeeds. Gate with a
      block-until-both-in-flight signal, **not** `Start-Sleep`.
    - **#51 dead-end incl. action-fails-and-writes-out-of-scope:** an attempt whose action FAILS
      *and* wrote out-of-scope — assert the out-of-scope file is reverted (or the un-revertable
      case fails the attempt with the audit log), and attempt 2 starts clean. (Today revert is
      skipped on action-fail — this test pins the corrected behavior: §2.5 routes both fail modes
      through the same honest-halt; **decide** in impl whether to revert on action-fail too —
      recommended yes, since a failed flailing action is exactly #51.)
    - **honesty mirror:** a gamed guardrail's out-of-scope edit is reverted to pristine, but a
      genuinely-wrong impl still fails its OTHER (in-scope) guardrail — restores the deleted
      `StateFlowTests` end-to-end #51 honesty intent.
    - **gated overlap-serialization:** two overlapping-scope tasks block-until-release (assert
      mutual exclusion via a gate, not speed); `MaxParallelism_Caps` asserts `== N`, so a
      strictly-serial scheduler FAILS.
  - `WorkspaceScopeEnforcerTests.cs` / `ScopeRevertTests.cs` — residual-region detection;
    baseline-only restore (no git); un-revertable → failed attempt + log line; rename =
    delete-old+create-new; case-only rename is not a phantom; symlink/junction not followed.
  - `ScopeValidationTests.cs` — GR2015 containment+empty-skip; GR2018 (`workingDirectory` +
    narrow scope).
  - `ScopeLockTests.cs` — held-set snapshot excludes caller; release by handle/identity (the #93
    value-equality fix) tested adversarially with two equal-by-value distinct scopes.
  - Done when: every new test FAILS against the current branch enforcer (proving non-tautology),
    per house TDD doctrine.
- **Then implementation** (`guardrails-harness-developer`), filesTouched under `src/`:
  - `Execution/WorkspaceScopeEnforcer.cs` — residual-region model (§2.1/§2.2); accept `liveScopes`;
    delete `TryRestoreFromGit`/`NormalizeGitObjectPermissions`/`MatchGlob` (call
    `WriteScope.IsInScope`); lazy/violation-bounded baseline (§2.4 + DA-counter-3); try/catch +
    `scope-enforcement.log`; symlink-skipping walk; same-comparer hash keys.
  - `Execution/WriteScope.cs` — canonical literal-prefix matcher; `IsInScope` from the shared
    helper; value equality OR keep struct + handle in ScopeLock.
  - `Execution/ScopeLock.cs` — expose held-set snapshot (excluding caller); `Acquire` returns a
    hold handle carrying its scope; `Release(handle)`.
  - `Execution/Scheduler.cs` / `TaskExecutor.cs` / `ITaskExecutor` — thread `liveScopes` from the
    lock to `RevertOutOfScope`; route un-revertable through `FailedAttempt`; revert-on-action-fail
    decision.
  - `Loading/PlanValidator.cs` + `Loading/DiagnosticCodes.cs` — GR2015 directional containment +
    empty-ancestor skip; new GR2018.
  - `State/RunReset.cs` — delete `scope-baseline/<id>` on single-task reset; drop `captured/`.
  - `Model/RunConfig.cs` — `enforcementIgnore` unchanged (already present).
  - Done when: build + full suite green on 3-OS CI; the §8 tests that FAILED now PASS.

### Stage 3 — Independent verification (`guardrails-devils-advocate` + lead)
- "cheapest wrong enforcer that passes these tests" pass; confirm the #88 test genuinely fails on a
  whole-workspace enforcer.

### Stage 4 — Re-dogfood capstone (`guardrails-skill-author` regenerate; lead runs)
- filesTouched: regenerate `docs/plans/05-disjoint-scope-ownership/` (the task folder) under the
  verified harness — `writeScope` on every task, no `captureHashes`/`tests-untouched`/`restoreOnRetry`,
  `enforcementIgnore` in `guardrails.json`; the enforcer-wiring guardrail is **structural/behavioral**
  (a real-attempt invocation assertion), not a keyword-grep (#93 item 21).
- Run from the global tool / `dotnet run` Debug build (Release self-lock memory). Done when: the
  dogfood completes green AND a human confirms it actually exercised concurrent enforcement (the
  journal shows ≥2 disjoint tasks overlapping in time).

---

## Proposed plan-document edits

I propose (you approve, then I apply):

1. **`docs/plans/06-scope-enforcement-remediation.md`** — this document (written to the worktree,
   not committed).
2. **`docs/plans/02-schemas-and-contracts.md`** — the §6 edits above, applied verbatim in the same
   change as the harness work begins (Stage 1). Until then they live here as the spec.
3. **`docs/plans/05-disjoint-scope-ownership.md`** — add a banner under its Status block:
   *"Superseded-in-part by plan 06 (scope-enforcement remediation): the enforcer diff/revert domain
   is per-task-scoped, not whole-workspace (#88); `git checkout --` is removed from revert (#93);
   `workingDirectory` + narrow scope is rejected (#90). See `06-scope-enforcement-remediation.md`."*
   Plan 05's milestone decomposition stays as history.
4. **`docs/plans/03-roadmap.md`** — the §5-step-3 reconciliation: bet #1 retired-to-plan-05/06,
   risk #2 rewritten to enforced disjoint `writeScope`, `exclusive` mention dropped, bet #5 (#78)
   restored (this is the Stage-0 reconciliation, not a new edit).

No code, no commits — design only.
