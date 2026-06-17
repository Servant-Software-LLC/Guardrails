# Plan 05 — Disjoint-Scope Concurrency + Write-Scope Ownership

> **Status:** design-of-record for the parallel-execution feature. This is the input to
> `/plan-breakdown`. Supersedes the v2 "worktree-per-task parallelism" bet in
> `03-roadmap.md` (retired — see §11). Closes the intent behind #54 and #62/#63.

## 1. Context & goal

The scheduler is already a parallel executor — Kahn in-degree counting feeds a
`Channel<TaskNode>` consumed by `maxParallelism` workers (`Scheduler.cs`). But prompt
actions default to `exclusive: true` and serialize on a global `WorkspaceLock`
(`Scheduler.cs` ~L155), so a typical all-prompt plan-breakdown DAG runs at **effective
parallelism 1** no matter how wide its independent branches are.

**Goal:** make independent tasks genuinely run concurrently, *soundly*, on a shared
workspace — with no git worktrees, no merge-back, no conflict resolver. We achieve this
by having each task declare the workspace paths it **owns** (its `writeScope`); the
scheduler runs concurrently only tasks whose scopes are disjoint, and the harness
**enforces** the declaration (reverting + failing any out-of-scope write).

This same ownership rule **subsumes the test-protection triad** (`captureHashes` +
`tests-untouched` + `restoreOnRetry`): an implementation task that doesn't own the test
files simply cannot modify them — the harness reverts the edit and fails the attempt.
Three mechanisms collapse into one: *a task may only modify what it owns.*

### Why not worktree-per-task (the rejected alternative)

A four-agent design review of worktree-per-task found four load-bearing soundness
problems: post-merge atomicity (a failed re-verify leaves the **shared** workspace
half-merged), non-idempotent re-verification (a fresh worktree re-applies `autocrlf`),
unsound AI conflict-resolution (the merging task's guardrails don't cover the clobbered
sibling's hunk), and a tautological cross-task state channel. Disjoint-scope **prevents**
conflicts by construction instead of **resolving** them after the fact, and preserves the
existing "a task mutates state/workspace only on success" atomicity unchanged.

## 2. Locked decisions (product owner)

1. **Mechanism = disjoint `writeScope`.** A task declares the workspace paths it may
   write; two tasks run concurrently iff their write-scopes are disjoint. No worktrees,
   no merge, no git requirement.
2. **Checked runtime enforcement in v1** (not trust-only). The harness snapshots the
   workspace, and after the action **reverts any out-of-scope write to its pre-attempt
   bytes and fails the attempt.** Enforcement makes disjointness *guaranteed*, not
   *trusted* — soundness-by-construction for the write/write case.
3. **No back-compat / pre-v1.** Only the author depends on the tool today. The
   test-protection triad is **removed cleanly** — no deprecation window, no migration of
   old plan folders. Because the triad is gone, `writeScope` + the GR2015 guard is the
   **sole** test protection, so **GR2015 is a hard error** (or `plan-breakdown` is
   guaranteed to emit an excluding scope).
4. **Cross-task data = what upstream produced.** A downstream task reads an upstream's
   real outputs (state fragment via `GUARDRAILS_STATE_IN`, produced workspace files),
   which already works. The `actionExitCode`-in-state channel (#63) is **not** built — it
   is tautological (always 0 where readable).
5. **Concurrency dial = the existing `maxParallelism`.**
6. **Full vertical:** harness (scope lock, enforcement) + `plan-breakdown` (emits scopes +
   read-after-write edges) + `guardrails-review` (flags overlap / missing edges /
   narrowable scopes).

## 3. The unified ownership model

A task **owns** its declared outputs and may write nothing else:

| Owns | Mechanism | Status |
|---|---|---|
| Its **state keys** | single-writer-per-key (#48): a task may only write top-level `state.json` keys equal to its own id | shipped |
| Its **workspace files** | `writeScope` (this plan): declared globs; the harness reverts + fails any write outside them | new |

`single-writer-per-key` is a **state-model** invariant and is **NOT** subsumed or changed
by this work — `writeScope` governs files, not state keys. They are siblings, not the
same rule.

## 4. Mechanism A — `writeScope` + `ScopeLock` (concurrency)

### 4.1 The field

`task.json` gains `writeScope`: an ordered list of **workspace-relative globs** the task
may create/modify/delete.

- **Absent/empty `writeScope` = universal (`**`) = "writes anywhere" = today's
  `exclusive`.** This is the safe default: a task that declares nothing serializes with
  everything, so an un-annotated plan behaves exactly as today. Concurrency is **opt-in**
  — earned by declaring a narrow scope.
- Glob subset: literal segments, `*` (within a segment), `**` (any depth). A bare
  `dir` or `dir/` means `dir/**`. **No** `?`, brace-expansion, or negation in v1
  (negation makes intersection undecidable-in-practice).
- Scopes must stay inside the workspace (reuse `WorkspaceContainment`).

### 4.2 Concurrency rule

Two tasks A and B run **concurrently** iff **both**:
1. **No DAG path between them** — neither transitively depends on the other (already
   enforced by the scheduler's `pendingDeps` readiness; a dependent is never offered to
   the channel until its ancestors are green), and
2. **Write-scopes disjoint** — the new `ScopeLock` admission gate.

### 4.3 `ScopeLock`

`WorkspaceLock` generalizes to **`ScopeLock`** (the binary shared/exclusive lock is a
special case: `**` = exclusive). Same FIFO-fairness discipline; admission keyed on
**scope intersection with currently-held scopes**: a waiter with scope S enters iff S does
not intersect the union of held scopes **and** no earlier FIFO waiter is blocked
(strict FIFO, no skip-ahead → starvation-free, deterministic). `maxParallelism` caps the
worker count independently; the two gates compose.

### 4.4 Overlap algorithm — conservative ("when in doubt, serialize")

A pure function `WriteScope.Overlaps(a, b)` next to `WorkspaceContainment`:
1. Universal short-circuit: if either side contains `**`, they overlap.
2. Pairwise glob comparison: walk segments in lockstep; two literals overlap iff equal;
   `*`/`**` overlaps any literal; `**` absorbs the tail.
3. **Conservative bias:** anything the walker can't *prove* disjoint is treated as
   **overlapping** (= serialize). A false "overlap" only costs throughput; a false
   "disjoint" would cost correctness. Sound for safety, incomplete for throughput — the
   right side to err on. Pure, deterministic, no I/O.

### 4.5 `exclusive` migration (no back-compat — clean)

Since we don't preserve back-compat, `exclusive` is **removed** and re-expressed as
`writeScope`:
- `exclusive: true` ⇒ `writeScope: ["**"]` (universal).
- A script's old "shared" default (concurrent with other shared tasks) is simply: declare
  a narrow scope, or accept universal. The default for any task with no `writeScope` is
  universal (serializes) — safe. `plan-breakdown` declares scopes to earn concurrency.
- The `exclusive` field is deleted from the schema and the loader (clean removal).

## 5. Mechanism B — checked enforcement with revert

After the action runs (and before guardrails), the harness verifies the task wrote only
within its `writeScope`, and **reverts** anything outside it.

### 5.1 The seam

In `TaskExecutor.RunAttemptAsync`, replacing the current `captureHashes` block:
```
preImage = WorkspaceScopeEnforcer.Snapshot(workspace, writeScope, enforcementIgnore)  // pre-action
... run action ...
if !action.Succeeded -> fail (unchanged)
violation = WorkspaceScopeEnforcer.RevertOutOfScope(workspace, writeScope, preImage)
if violation.HasOutOfScopeWrites:
    // revert already applied; FAIL the attempt (honest halt)
    return FailedAttempt("out-of-scope writes reverted: <paths>")
... guardrails (unchanged) ... merge (unchanged) ...
```
A new `WorkspaceScopeEnforcer` collaborator (mirrors `CapturedFileStore`/`FileHashCapture`
factoring; `TaskExecutor` stays the orchestrator; reuses the `FileHashCapture` SHA-256
primitive and `WorkspaceContainment`).

### 5.2 The "workspace tree" — what's diffed

The snapshot/diff walks the workspace but **excludes** an `enforcementIgnore` set
(configurable in `guardrails.json`, with defaults): `state/` (harness-owned, mutated every
attempt), `.git/`, and build artifacts (`**/bin/**`, `**/obj/**`, `**/node_modules/**`).
Excluded paths are **never** reverted (a build guardrail legitimately writes `bin/`).
Explicit list; **no** `.gitignore` parsing in v1 (predictable, plain-files-honest).

### 5.3 Detection + revert semantics

- **Detection is content-based** (SHA-256 hash diff), not mtime — a no-op touch is not a
  violation (matches `captureHashes`'s content semantics). (mtime may be used only as a
  *skip-hashing hint*, never as the violation signal.)
- **Revert only out-of-scope changes; keep in-scope changes** (preserves the existing
  failed-attempt behavior — in-scope edits stay so the next attempt's "fix, don't restart"
  feedback works):
  - created out-of-scope file → delete it,
  - modified out-of-scope file → restore pre-attempt bytes,
  - deleted out-of-scope file → restore pre-attempt bytes.
- **Byte baseline source:** for a tracked file, `git checkout -- <path>` (no harness byte
  store needed); for an **untracked** out-of-scope file (the #51 case — authored test
  files are often untracked), a lazy byte snapshot into `state/scope-baseline/<path>`
  (harness-owned, `--fresh`-wiped — the `state/captured/` precedent).
- Every revert (and any file the harness could **not** revert) is logged to
  `scope-enforcement.log` in the attempt dir (loud-on-failure audit, the #51 FIX-D
  precedent). Containment re-checked before every write (`WorkspaceContainment`).
- **Cancellation:** if cancelled during the action → existing path (journal pending, no
  revert; resume backstops). The revert is idempotent; a partial revert is completed by
  the next attempt's pre-action snapshot.

### 5.4 Self-healing across retries

Each attempt reverts its own out-of-scope damage **before it ends**, so the next attempt
starts clean. This is exactly the `restoreOnRetry` guarantee for the #51 dead-end, but
driven by ownership instead of a separate captured baseline — and it covers *every*
out-of-scope path, not just declared test files.

## 6. Mechanism C — the consolidation (retire the test-protection triad)

`writeScope` + revert **subsumes** the triad. Validated subsumption (one gap, closed by
GR2015):

| Old protection | Replaced by | Note |
|---|---|---|
| `tests-untouched` (impl task must not edit declared test files) | impl task's `writeScope` excludes `tests/**`; an edit there is out-of-scope → reverted + fail | **Stronger:** protects *all* out-of-scope files, not just declared test files |
| `restoreOnRetry` (restore dirtied test files before retry) | revert restores out-of-scope writes at end of attempt → next attempt clean | covers the untracked-file #51 case via `state/scope-baseline/` |
| `captureHashes` (content-hash detection feeding tests-untouched) | content-based enforcement diff | the hashing primitive is reused, not deleted |
| **#48 single-writer-per-key** | **NOT subsumed** | state-model invariant; kept verbatim |

**The one gap (and its guard).** `tests-untouched` protected the tests *regardless* of how
the implementation task was written (it was anchored to the **test-author's**
declaration). `writeScope` anchors protection to the **implementation task's own** scope —
so a universal/absent-scope implementation task would leave the tests unprotected. Because
we have **no back-compat** and remove the triad entirely, this guard is **non-optional and
strict**:

- **GR2015 (ERROR):** a task that depends on a test-author task but declares no
  `writeScope` excluding that ancestor's outputs. Fails `validate`/CI. (Strict error, not
  a warning, precisely because `writeScope` is now the only test protection.)
- `plan-breakdown` always emits an excluding `writeScope` on implementation tasks (§7).
- `guardrails-review` flags a universal/absent scope on a task with editable upstream
  tests.

The test-author task no longer needs `captureHashes` for protection — it simply **owns**
`tests/**`. Its real guardrail stays **tests-fail-on-current-code** (anti-tautology),
unaffected.

## 7. Skills (full vertical)

**`plan-breakdown`:**
- Emit `writeScope` per task, derived from what each task *produces* (the same output
  analysis it already does for guardrails). An implementation task building `src/Feature`
  ⇒ `writeScope: ["src/Feature/**"]` (excluding the test-author's `tests/Feature/**`).
- **Conservative default:** when the skill cannot confidently bound a task's writes (a
  broad refactor), it **omits** `writeScope` (= universal = serializes). Never guess
  narrow — a wrong-narrow scope is an unsoundness; an omitted scope is merely slow.
- Add `dependsOn` edges for read-after-write: if task B's narrow-scoped action reads
  files task A produces, emit `B dependsOn A`.
- **Stop emitting** `captureHashes` / `tests-untouched` / `restoreOnRetry`.

**`guardrails-review`:** flag (a) overlapping scopes among independent tasks (lost
parallelism), (b) a task whose scope could be narrowed to gain concurrency, (c) a
narrow-scoped task whose prompt references files another independent task writes (missing
read-after-write edge — the heuristic catch for §6's gap).

## 8. Schema / contract changes (SSOT `02-schemas-and-contracts.md`)

- **§3 task.json:** add `writeScope`; **remove** `exclusive`, `captureHashes`,
  `restoreOnRetry`.
- **New §3.2 "writeScope — enforced disjoint ownership":** the disjointness rule;
  absent/empty = universal; the glob subset + conservative overlap algorithm; the
  revert/enforcement semantics; the read-after-write contract ("a task that reads
  another's output MUST `dependsOn` it; write-scope protects writers, the DAG protects
  readers"); GR2015.
- **§5.3 "harness writes the workspace":** today "exactly one case" (restoreOnRetry) — now
  **exactly one case (the scope-revert)**, since restoreOnRetry is removed and replaced.
  Containment analysis for the revert write path.
- **§2 guardrails.json:** add `enforcementIgnore` (defaults) ; remove anything tied to
  the retired triad.
- **§6.1 `--fresh`/`reset`:** wipe `state/scope-baseline/` (replacing `state/captured/`).
- **§3.1/§3.1.1:** delete the captureHashes/restoreOnRetry sections.
- Validation: GR2015 (subsumption guard, ERROR), GR2016 (overlapping scopes among
  independents — warning), GR2017 (malformed glob — error). Reuse `WorkspaceContainment`
  for escape checks.

## 9. Honest findings carried into the build

- **§6 gap → GR2015 (error):** the only residual soundness concern; closed by strict
  validation + skill doctrine. Do not consider the triad retired until GR2015 ships.
- **#48 stays:** state ownership is a separate invariant; not touched.
- **Revert blast radius:** revert *writes* the workspace (bigger than a read-only
  guardrail). It only ever restores *pre-attempt bytes* of *out-of-scope* paths (never
  synthesizes content); in-scope files are provably never touched. This is why **detection
  ships before revert** (M4 before M5) — the dangerous write path lands after detection is
  trusted.
- **Enforcement walk cost** on a large workspace is the v1 shared-workspace tax;
  `enforcementIgnore` is the lever. (A future worktree story would make scope physical and
  retire the walk — explicitly out of scope here.)

## 10. Milestones (walking-skeleton first; the `/plan-breakdown` input)

Each milestone is independently shippable and testable. Test-author tasks precede
implementation tasks (TDD-by-default). The harness builds these in dependency order.

- **M1 — `WriteScope` type + overlap function (the pure core).**
  Scope: `WriteScope` value type (parse, universal sentinel, glob subset) + `Overlaps(a,b)`
  pure function, next to `WorkspaceContainment`.
  Exit: an exhaustive overlap truth-table passes — `["src/A/**"]` vs `["src/B/**"]` =
  disjoint; vs `["**"]` = overlap; `dir` ≡ `dir/**`; sibling `src/FeatureX` does NOT match
  `src/Feature/**`. Unit-only, no I/O. Size: S.
  Key files: `src/Guardrails.Core/Execution/WriteScope.cs`, tests.

- **M2 — `ScopeLock` + scheduler rewire (the concurrency win).**
  Scope: `ScopeLock` (generalize/replace `WorkspaceLock`); rewire `Scheduler` to resolve a
  task's scope and admit on disjointness; remove `exclusive`.
  Exit: a 3-independent-narrow-scope-task plan runs them concurrently (a fake runner
  records overlapping execution windows); two universal-scope tasks do not overlap.
  Depends on M1. Size: M.
  Key files: `Execution/ScopeLock.cs` (replaces `WorkspaceLock.cs`), `Execution/Scheduler.cs`,
  `Model/TaskNode.cs`, loader.

- **M3 — validation (GR2015 error / GR2016 warning / GR2017 error).**
  Scope: the three diagnostics + checks (subsumption guard, independent-overlap, malformed
  glob), reusing `DependencyGraph` for "no DAG path between A and B" and
  `WorkspaceContainment` for escapes.
  Exit: a task depending on a test-author with no excluding scope → GR2015 **error** at
  validate; independent overlapping scopes → GR2016 warning; `?`/brace glob → GR2017 error.
  Depends on M1. Size: S/M.
  Key files: `Loading/DiagnosticCodes.cs`, `Loading/PlanValidator.cs`, tests.

- **M4 — enforcement, detect-only.**
  Scope: `WorkspaceScopeEnforcer` snapshot + post-action detect (content hash diff,
  `enforcementIgnore`); **fail** an attempt on an out-of-scope write (no revert yet).
  Exit: task B (scope `src/**`) that edits `tests/x` → attempt fails "out-of-scope write";
  an in-scope edit passes; a no-op touch passes. Depends on M1. Size: M.
  Key files: `Execution/WorkspaceScopeEnforcer.cs`, `Execution/TaskExecutor.cs`,
  `Model/RunConfig.cs` (`enforcementIgnore`), SSOT §3.2/§2.

- **M5 — enforcement revert (subsumes restoreOnRetry).**
  Scope: revert out-of-scope create/modify/delete to pre-attempt bytes (git baseline for
  tracked, lazy `state/scope-baseline/` for untracked); `scope-enforcement.log`;
  `--fresh`/`reset` wipe; cancellation handling; SSOT §5.3 rewrite.
  Exit: the #51 scenario — task edits an out-of-scope test, attempt 1 fails, **attempt 2
  starts clean and succeeds** when the impl is correct; a deleted out-of-scope file is
  restored. Depends on M4. Size: L.
  Key files: `Execution/WorkspaceScopeEnforcer.cs`, `Execution/TaskExecutor.cs`,
  `State/RunReset.cs`, SSOT §5.3/§6.1.

- **M6 — skills switch-over (full vertical).**
  Scope: `plan-breakdown` emits `writeScope` + read-after-write edges and stops emitting
  the triad; `guardrails-review` adds the overlap / narrowable / missing-edge flags;
  catalogue + example-breakdown + schemas updated; golden round-trip re-proven.
  Exit: `/plan-breakdown` on a TDD plan produces `writeScope` on the impl task, no
  `tests-untouched`, validates clean (GR2015 satisfied); review flags a deliberately
  overlapping pair. Depends on M3, M5. Size: M.
  Key files: `.claude/skills/plan-breakdown/**`, `.claude/skills/guardrails-review/**`,
  `.claude/skills/guardrails-domain-knowledge/**`.

- **M7 — retire the triad (clean removal; gated on M3 + M5).**
  Scope: remove `captureHashes`/`tests-untouched`/`restoreOnRetry` from the schema,
  loader, `FileHashCapture` capture-into-fragment path, `RetryPolicy` tests-untouched
  feedback, and `CapturedFileStore` (or rename/retain only the byte-store reused by the
  enforcer); delete the SSOT §3.1/§3.1.1 sections; regenerate the dogfood plan
  (`04-dogfood-cost-cap`) to the new pattern.
  Exit: no `captureHashes`/`tests-untouched`/`restoreOnRetry` references remain; full
  suite green; dogfood plan regenerated and validate-clean. #48 single-writer untouched.
  Depends on M5, M6 (and the GR2015 guard from M3). Size: M.

**Dependency summary:** M1 → {M2, M3, M4}; M4 → M5; {M3, M5} → M6; {M5, M6} → M7.
M2 (concurrency) and the M4/M5/M7 (ownership) tracks share the `writeScope` field but are
otherwise independent — both shippable.

## 11. Roadmap impact

Retire `03-roadmap.md` v2 bet #1 (worktree-per-task parallelism) — replaced by
disjoint-scope concurrency (this plan, v1.x; no git, no merge). Update risk-register
item #2 ("parallel tasks sharing one workspace") to point at enforced disjoint
`writeScope` rather than worktrees.

## 12. Verification / Reality Gate

- Unit: `WriteScope.Overlaps` truth table; `ScopeLock` admission/fairness; enforcer
  snapshot/diff/revert; GR2015/2016/2017 diagnostics.
- Integration: a real multi-task plan where independent narrow-scope tasks run
  concurrently (wall-clock asserted vs serial); the #51 retry-recovery end-to-end; an
  out-of-scope write reverted + failed; the regression suite pinning #48 (single-writer)
  and the absence invariant (unchanged).
- Skills: `/plan-breakdown` round-trip emits `writeScope`, no triad, GR2015-clean;
  `/guardrails-review` flags an overlapping pair.
- 3-OS CI green throughout.
- **Dogfood (the headline Reality Gate):** this plan is itself broken down and executed by
  the harness (running from an installed `preview.19`, against an isolated worktree) to
  implement all seven milestones — the tool building its own parallel-execution feature.
