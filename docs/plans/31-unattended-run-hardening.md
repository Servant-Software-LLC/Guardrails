| 7 | **GR2069** | the four `Loading/…` files, every one reachable, but no single task holds all four: `21-implement-reachability-gate` holds THREE (`PlanLoader.cs`, `PlanValidator.cs`, `DiagnosticCodes.cs`) and lacks `RawManifests.cs`, which only `09-add-openai-block-config-surface` writes |# 31 — Unattended-run hardening: cheap escalations, unsatisfiable plans, and edits during a live run

**Status:** reviewed-quality draft, for inline review as a draft PR before any breakdown.
**Issues:** #554, #553, #545 (part 3 only — parts 1 and 2 shipped in `1952c9b`).
**Found by:** the project's first unattended overnight run — plan 28, run `2026-08-30T18-32-07Z-00c2`.
**Intended execution:** this plan is itself run unattended (§9).

> **One night, three defects, one theme.** Plan 28 ran overnight with `--autonomous`. It produced 30
> tasks of real work and seven needs-human escalations. Every escalation was *correct to fire* — the
> honest-halt gate did its job. What the night exposed is that the harness is expensive at exactly the
> three moments an unattended run depends on most: it **throws away** the work of an escalating attempt,
> it **cannot see** a plan that is unsatisfiable before spending a dollar on it, and it is **silent**
> when the operator edits the plan folder underneath a run that is still going.

---

## 1. What one unattended night cost

| | Measured, plan 28 |
|---|---|
| Work discarded by `needs-human` escalations | **$22.02** across six tasks (#554) |
| Redo spend on the attempts that followed them | $19.85 (a material fraction is re-derivation) |
| Sharpest single case | task `15-implement-runner-verdict-roles`: **$10.91** discarded |
| Escalations caused by a task told to deliver an outcome its `writeScope` could not reach | 4 of 7 (#553) |
| Cost of the two worst instances | $13.33 / 19 tasks blocked (run 1) and $3.84 / 21 tasks blocked (run 3) |
| Escalation fixes that required editing the plan folder | **7 of 7**, each hand-sequenced *after* the run exited to dodge the drift hazard (#545 part 3) |

The three numbers are independent. #553 attacks escalation **frequency**; #554 attacks escalation **unit
cost**; #545 part 3 attacks the operator discipline that fixing an escalation currently demands. §6 records
why the first two are not the same fix, because a later reader will otherwise conflate them.

---

## 2. Scope, ordered — and the order is decided

1. **#554 — preserve on the `needsHuman` path.** Cheapest fix, largest measured cost. It reuses machinery
   that already exists and is already proven; nothing about it is new mechanism.
2. **#553 — `GR2068`, the handoff-coverage check.** Prevention beats recovery: it runs at `validate` time,
   costs nothing, and cannot be skipped.
3. **#545 part 3 — warn when a plan folder is edited during a live run.** The root cause behind all seven
   of plan 28's fix cycles.

The order is not up for re-litigation. It is cheapest-first by (measured cost ÷ implementation risk), and
each stage is independently shippable — §13 stages them so a halt in one does not strand the others.

> **Why #545 part 3 is not moved earlier.** An adversarial pass argued it should lead, because a live
> plan-edit warning "would have paid for itself during plan 31's own night." It would not. The harness
> **executing** plan 31 is the installed `v1.12.0` global tool; nothing this plan builds is in that
> process, so no stage can change the behaviour of the run that builds it. The argument would hold only
> for the *next* plan, which is what the ordering above already optimises for. With GR2068 landing as a
> WARNING (§4.6), the residual risk-ordering concern is gone too.

**Placement.** All three are **harness** changes (`Guardrails.Core`, one CLI rendering touch). None is a
skill change. One is a schema change (§12). Nothing here is a v2 bet.

---

## 3. #554 — the `needsHuman` path never preserves

### 3.1 The machinery that already exists, and must be reused rather than reinvented

Retry salvage (#195 / #306, SSOT §3.2) is shipped and works. On the retry path
`TaskExecutor.RunAttemptAsync` calls one private helper —
`TryStashFailedAttempt(TaskNode task, WorktreeHandle worktree, int attemptNumber)` — which does three
things: `GitWorktreeProvider.PreserveAttemptToRef` (a throwaway-index snapshot to
`refs/guardrails/<taskId>/attempt-<N>`), `DiffStatAgainstBase` + `DiffAgainstBase`, and
`AttemptArtifacts.WriteSalvagePatch` (writes `prior-attempt.patch` into the attempt's log dir). It returns
a `SalvageRef(RefName, DiffStat, Attempt, PatchPath)`, and `RetryPolicy.AppendSalvageSection` renders the
"Prior attempt work is salvageable" section into `feedback.md`, which the next attempt's composed prompt
inlines verbatim.

Verified on plan 28: attempts 1, 2, 3, 4, 6 and 8 of task 28 each carry a patch of 34KB–76KB and each
`feedback.md` names it.

### 3.2 What the escalation path actually does — the correction the issue does not make

`TaskExecutor.cs:837-843` short-circuits before any salvage call:

```csharp
// --- needsHuman short-circuit (SSOT §9): record + escalate IMMEDIATELY -----------
if (action.NeedsHumanQuestion is { } question)
{
    return _journaler.NeedsHuman(
        task, attemptNumber, startedAt, relativeLogDir, logDir, action, question,
        action.NeedsHumanOptions, action.NeedsHumanKind, provenance: provenance);
}
```

Same plan-28 evidence from the other side: attempts **5** and **7** of task 28 — the two `needs-human`
ones — have no `prior-attempt.patch` and no salvage section, while every neighbouring attempt does.

**"The work is discarded" is not what happens, and the truth is worse.** The attempt loop returns
terminally at `TaskExecutor.cs:275-293` on `TaskOutcome.NeedsHuman`, *before* the F2 reset at `:433`. So
the escalating attempt's tree is **not** reset in place. It is **orphaned**:

- a resume generates a **new `runId`** and calls `CreateSegment(…, attempt: 1, …)` at `planHead` — a
  fresh segment, not the old one;
- `reuse` and `fork` are **intra-run** worktree policies and never reach across runs;
- `reclaim` only deletes after the 24-hour staleness threshold.

So the tree is never handed back to anybody. It is garbage that survives on disk for a day and then goes
away. Plan 28's attempt 8 is what that looks like from the agent's side: it started from zero against a
6,537-line SSOT and died on `max-turns`.

**This strengthens #554 rather than softening it.** The earlier framing ("the next run's worktree
acquisition might take it") left room for a reader to conclude the work is sometimes recoverable and the
fix is a convenience. It is not: on this path the work is **unreachable by construction**, and the ref +
patch are the *only* durable artifact anyone — the resumed agent, the triaging human, the firstmate — can
be pointed at. Nothing points at it today, and the operator deciding how to unblock is told what is wrong
and nothing about what is already built. Attempt 7's escalation enumerated its completed content work in
detail. None of it was reachable.

### 3.3 The change, seam by seam

| Seam | Change |
|---|---|
| `TaskExecutor.RunAttemptAsync` (`:837-843`) | Before `_journaler.NeedsHuman(...)`, call `TryStashFailedAttempt(task, worktree, attemptNumber, restrictToScope: task.WriteScope)` — guarded by the existing `IsRealGitSegment(worktree)` predicate, **not** by `StashIfRollingBack` (§3.4 divergence 1). Pass the `SalvageRef?` into the journaler. |
| `GitWorktreeProvider.PreserveAttemptToRef` (`:1373-1398`) | Gains an optional `IReadOnlyList<string>? restrictToScope = null`. When non-null, between the **staging call** at `:1383` and `git write-tree` at `:1384` it lists the staged set (`git diff --cached --name-only <taskBase>`, same `GIT_INDEX_FILE` env) and runs `git reset --quiet <taskBase> -- <paths>` for every path where `WriteScope.IsInScope(path, restrictToScope)` is false. `reset` — not `rm --cached` — because it restores the `taskBase` blob for a modified or deleted file and drops the entry for an added one, which is all three cases in one command. **The retry path passes `null` and is byte-identical to today** (§3.4 divergence 3). |
| `AttemptJournaler.NeedsHuman` | Gains a `SalvageRef? salvage` parameter. It has exactly **one** caller (`TaskExecutor.cs:840`), so this is not a source break and needs no test-fixture edit. It appends the salvage section to the `feedback.md` body it already composes. |
| `RetryPolicy.AppendSalvageSection` (`:438-538`) and `AppendHeader` (`:983`) | `private static` → `internal static`, plus an optional `SalvageFraming framing = SalvageFraming.Retry`. **One owner of that text**, three framings: `Retry` (today's bytes, unchanged), `Escalation` (no rollback claim — §3.4 divergence 2), `PriorAttempt` (the compact routing block §3.5 needs). `AppendHeader`'s existing four-way branch gains a fifth for preserved-but-not-rolled-back. |
| `PriorAttemptRef` (`Prompts/PromptContext.cs`) | Gains two optional init-only members: `SalvagePatchPath` and `SalvageRefName`. Optional, so no existing construction site breaks. |
| `DependencyContextBuilder.BuildPriorAttempts` | Already walks the journal and already knows each prior attempt's `LogDir`. It fills the two new members by **probing `File.Exists(logDir/prior-attempt.patch)`** and deriving the ref name from `taskId` + attempt number. **No journal schema change** — the patch file's existence is the record. |
| `PromptComposer.AppendPreviousAttempt` (`:298-318`) | Today a flat bullet list of log paths with no recovery guidance at all. For a prior attempt carrying a patch it now calls `AppendSalvageSection(..., SalvageFraming.PriorAttempt)` — the same owner, never a second copy. |
| `Scheduler.BuildGateContext` (`:3079-3084`) | The escalation `Context` string names the salvage ref and patch path when the escalating attempt left one. This is what a human — or a firstmate answering the escalation — reads. |

> **There is no literal `git add -A` in `PreserveAttemptToRef`, and an implementer must not go looking
> for one.** Line `:1383` is
> `GitInWithEnv(worktreePath, env, SegmentStaging.StageAllArguments().ToArray());` — the `add -A -- .`
> pathspec with its three `:(exclude,glob)` terms is built in `SegmentStaging.StageAllArguments()`, in
> `src/Guardrails.Core/Execution/SegmentStaging.cs`. **That file is deliberately NOT in §13 stage 2's
> `writeScope`:** it is shared with the segment-commit path, so changing its pathspec would alter every
> segment commit in the harness. The mechanism above needs no change to it — stage everything exactly
> as today, then `reset` the out-of-scope paths back out of the index. An earlier revision of this row
> said "the `git add -A` stage", which reads as an invitation to edit the staging arguments; that is a
> retry burned on the plan's own wording, so the row now names the call it actually means.

**Why the framing parameter and not new text.** `AppendSalvageSection`'s output is hard-pinned in two
suites: `tests/Guardrails.Core.Tests/RetryPolicySalvageAdviceTests.cs` (the patch bullet must be **first**,
`git show "<ref>:<path>"` verbatim, `"EVERYTHING"` banned, no `git diff`/`git apply` invocation, the
`git -C` failure shape named) and `tests/Guardrails.Integration.Tests/RetrySalvageTests.cs` (the literal
heading `## Prior attempt work is salvageable`, the ref name, the protected-artifact suppression). A
default-valued framing parameter leaves the `Retry` branch emitting the same bytes, so **both suites pass
with zero edits** — which is why §13 stages 2 and 3 correctly carry no test paths. That property is itself
a guardrail on those stages, not a hope.

**Why `PriorAttemptRef` and not a new journal field.** The salvage ref name is fully derivable
(`refs/guardrails/<taskId>/attempt-<N>`) and the patch's presence is observable on disk. Journaling either
would create a second source of truth for a fact the filesystem already holds, and would need a migration.
It also generalises correctly: every prior attempt that left a patch is now routed in the composed prompt,
not only the escalating one.

### 3.4 Three deliberate divergences from the retry path

All three are traps an implementer mirroring the retry call site verbatim will fall into.

1. **The guard is `IsRealGitSegment`, not `WorktreeWillReset`.** `StashIfRollingBack` asks *"will this
   attempt be reset?"*. On the escalation path that question is wrong: no reset follows (§3.2), and on a
   *final* attempt `WorktreeWillReset` is false — yet a final escalating attempt is precisely the one
   whose work a human is about to build on. The escalation path preserves whenever there is a real git
   segment, **regardless of `isFinal`**.
2. **The feedback wording must not claim a rollback.** The `Retry` framing says the work was rolled back
   and saved. On this path nothing was rolled back; the honest sentence is that the tree that produced
   this work is **orphaned**, and the ref and patch are the only durable copies.
3. **The staged set must be filtered to `writeScope` — this one is a correctness fix, not a wording
   one.** The retry path reaches `TryStashFailedAttempt` at `:1258` only *after* the write-scope check
   (`:1090`/`:1101`) and `ScopedRevert` (`:1107`) have already run, so its tree is scope-clean before
   anything is staged. **The escalation short-circuit at `:837` is ~250 lines upstream of both**, and
   `PreserveAttemptToRef` stages `git add -A -- . :(exclude,glob)**/node_modules/**
   :(exclude,glob)**/.guardrails-staging/** :(exclude,glob)**/.guardrails-agent-io/**` with **no
   `writeScope` filter at all**, diffs it `--binary`, and commits via `commit-tree`, which runs no hooks.
   Stashing unfiltered would therefore write an escalating agent's **out-of-scope** edits into a durable,
   agent-readable patch that the next attempt is invited to apply.

**What divergence 3 structurally restores.** The retry path's protected-artifact protection is
`TaskExecutor.cs:1255-1257`:

```csharp
bool fileWritesRolledBack = WorktreeWillReset(worktree, isFinal);
bool protectedArtifactGamed = failed.Any(r => GuardrailArchetypes.IsProtectedArtifactCheck(r.Name));
SalvageRef? salvageRef = fileWritesRolledBack && !protectedArtifactGamed
```

On the escalation path `failed` is **empty** — no guardrail ran — so `protectedArtifactGamed` is
structurally `false` and that suppression is **inapplicable**, not merely bypassed. Filtering to
`writeScope` is what takes its place: a protected upstream test file the task is not authorized to write
is filtered out of the patch entirely. **The residual, stated:** a protected artifact that *is* inside the
task's own `writeScope` is still stashed. That is identical to the retry path's post-`ScopedRevert` state,
and the load-bearing guarantee is unchanged — the deterministic per-attempt re-check (write-scope check
plus every guardrail, re-run on the next attempt's FINAL state) re-fails a re-introduced gamed edit
regardless of how it got there (SSOT §3.2).

**Serial mode is unchanged.** `IsRealGitSegment` is false with no worktree, so nothing is preserved and
nothing is advertised — correct, because in serial mode the files are still on disk.

**The empty-diff guard stays exactly as-is.** An agent that escalates having written nothing has nothing
to salvage. Offering a "recover your work" section for an empty patch is worse than silence (§11). Note
the filter can *create* this case: an attempt whose every write was out of scope produces an empty
filtered patch and is correctly offered nothing.

**Ref growth is bounded.** Salvage refs are pruned only when a task's final settle is `succeeded`
(`Scheduler.cs:3483-3487`) or wholesale on `--fresh`/`reset` (`RunReset.cs:523` →
`PruneAllSalvageRefs`). #554 adds refs on precisely the tasks that by definition never succeed, so a
task escalating repeatedly across many resumes accumulates them until the next `--fresh`. `PreserveAttemptToRef`
therefore deletes `refs/guardrails/<taskId>/attempt-<M>` for `M <= N - SalvageRefRetentionPerTask` (a named
constant, ≥ the default retry budget) as it writes attempt `N`. Refs are throwaway bookkeeping; the
per-attempt patches in the log dirs are unaffected and remain the durable record.

### 3.5 Regression pins

The pin from #554, verbatim:

> A task whose action emits `needsHuman` **after** writing files must leave a non-empty
> `prior-attempt.patch` in that attempt's log dir and a salvage ref at
> `refs/guardrails/<taskId>/attempt-<N>`; the following attempt's composed prompt must name it.

Today the patch is absent, which is why the current tests pass — they only ever exercise salvage through
the retry path. Three clarifications the pin needs to be worth anything:

1. **The third clause is asserted on the composed prompt bytes**, not on `feedback.md`. The escalation
   path returns `FeedbackPath: null`, so the forward carry runs through `PriorAttemptRef`, not through the
   inlined-feedback route — a test reading `feedback.md` would pass with the composed prompt still silent.
2. **"Name it" is not enough — the prompt must carry the recovery ROUTING.**
   `PromptComposer.AppendPreviousAttempt:298-318` renders priors as a flat bullet list of log paths whose
   only instruction is "Read the transcript… and the feedback"; one more path bullet satisfies "names it"
   and changes nothing an agent does. The pin asserts the composed prompt contains the size-routed choice
   — read `prior-attempt.patch` for a small edit, `git show "<ref>:<path>"` for a new file — and the
   `writeScope` caveat, all emitted by `AppendSalvageSection`, not re-authored.
3. **An out-of-scope write is ABSENT from the patch** (§3.4 divergence 3). Asserted on the patch bytes: an
   escalating attempt that writes both an in-scope and an out-of-scope file leaves a patch containing the
   first and not the second, and the salvage ref's tree agrees.

And two guarding the shipped path: **the two existing salvage suites pass with zero edits**, and a
`needsHuman` on a **final** attempt still preserves — the pin that catches an implementation that copied
`StashIfRollingBack` verbatim.

---

## 4. #553 — `GR2068`: a plan whose tasks cannot write the files its own handoff table names

### 4.1 What is decidable, and from what — stated as a limit, not a claim

GR2068 is decidable from **the plan folder plus the plan document**, and **only for surfaces the plan
actually declared**. It does **not** catch a task whose guardrail needs a file the plan never named at
all. That residual gap is real and this plan does not close it.

**Read §4.6 before anything downstream.** It hand-runs the final rule against plan 28's own handoff
table, in both its broken and its fixed states, and reports the residual noise honestly rather than
tuning until the table looks good.

### 4.2 Locating the table — two gates that produce silence, not noise

1. **The plan document.** `BreakdownCommand.cs:112-115` builds `<dir>/foo.md` -> `<dir>/foo/`, so the
   sibling `.md` is the layout the CLI itself creates, and it holds for plans 27 and 28. It is **not
   universal**: `examples/parallel-hello/parallel-hello/` has no sibling `.md` at all. So it is a
   convention the check *relies on when present* and never guesses past. **v1 is sibling-only.** The issue
   floats a fallback that mines task prompts for the plan path; it is declined, because a wrong plan
   document produces a wrong diagnostic, which is the worst outcome a path-coverage check can have. No
   sibling `.md` ⇒ **silent**.
2. **The table.** Anchored on **content, not section number**: a markdown table one of whose column
   headers normalises to `filestouched` (case- and space-insensitive). No such table ⇒ **silent**.

The second gate is what keeps this check from being muted within a week. Most plans predate the
convention; a check that fires on every legacy plan gets turned off, which is the failure mode #229 warns
about. Adopting the convention is opt-in **by writing the column**.

### 4.3 Extraction — what counts as a candidate

A `filesTouched` cell is prose with paths in it. Plan 28 row 1 reads, in full:

> `Prompts/PromptInvocation.cs`, all **seven** §3.4 producers, **and `tests/**`**

Two narrowings:

1. **Only backtick-delimited code spans are candidates.** "all seven §3.4 producers" is deliberately not a
   path and must never be guessed at.
2. **Prose spans are silent.** A candidate with **no `/` and no file extension** is not a path — `required`
   and `writeScope` drop out; `RawManifests.cs` and `Journal/` survive. No extension allow-list, no case
   heuristics, no C#-member-access special case.

A trailing `:<line>` is stripped; a trailing `/` normalises to `/**`.

### 4.4 Resolution — the anchor test, and what the check refuses to judge

Two of plan 28's cells match **nothing** in the plan's write scopes, and they are entirely different
animals:

| Cell | What it is |
|---|---|
| `tests/Guardrails.Integration.Tests/FakeOpenAiServer.cs` (row 3) | a **stale path** — the file shipped one directory deeper, at `…/OpenAiCompat/FakeOpenAiServer.cs`. A real defect; reporting it is the point |
| `Cli/Commands/` (row 8) | an **unresolvable fragment** — the real path is `src/Guardrails.Cli/Commands/…`, so `Cli` is a *fragment of* the segment `Guardrails.Cli`, not a segment. The cell is too vague to check, and firing on it teaches nothing |

The discriminator is the **whole-segment anchor**:

> A candidate is **resolvable** when its **first path segment** equals a **whole path segment** of some
> `writeScope` entry in the plan. An unresolvable candidate is **dropped — silently**. The check declines
> to judge a cell that is not written in the plan's own path vocabulary.

`tests` is a whole segment of `tests/Guardrails.Core.Tests/…`, so row 3 stays checkable and fires. `Cli`
is a whole segment of nothing, so row 8's fragment is dropped and row 8 goes silent on its remaining,
resolvable candidate. `Loading` is a whole segment of `src/Guardrails.Core/Loading/PlanLoader.cs`, so
row 7 — the row #553 was written about — stays checkable.

> **This is not the "root-vocabulary gate" a previous revision removed, and the difference is the whole
> point.** That gate required a candidate's first segment to be the **first** segment of a `writeScope`
> entry, which muted `Loading/PlanLoader.cs` (nothing starts with `Loading`) — it silenced the motivating
> case. Requiring it to be a whole segment **anywhere** is the correct relaxation: it is exactly the
> premise the §4.5 suffix arm needs in order to resolve a relative cell at all. A candidate the suffix arm
> could never match is one the check should not be reasoning about.

### 4.5 Coverage — per row, by a SINGLE task

`WriteScope.IsInScope(path, scope)` **globs the `scope` side and splits `path` literally**
(`WriteScope.cs:74-98`). That is one direction, and the two candidate shapes need it pointed opposite
ways. Getting this backwards is the easiest way to ship a check that can never fire.

**Arm-match** — "does scope entry `e` cover candidate `C`":

| `C` | Covered by `e` when |
|---|---|
| **concrete** (no `*`) | `WriteScope.IsInScope(C, [e])` **∨** `e == C` **∨** `e` ends with `/C` (a **segment-aligned suffix**, never a substring) |
| **glob** (has `*`) | `WriteScope.IsInScope(e, [C])` **∨** `WriteScope.IsInScope(e, ["**/" + C])` — **arguments swapped**, the only direction the primitive supports |

The suffix arm and its `**/` glob analogue are what let a relative cell like `Prompts/PromptInvocation.cs`
or `Journal/` resolve against `src/Guardrails.Core/…` **without touching the repo tree** — which matters,
because a handoff table names files the plan is about to **create**, so resolution must never depend on a
file existing.

**No new primitive is built.** In particular there is no `WriteScope.Covers` (strict containment: "one
task is authorized for the whole glob"). It is too strict and would fire on row 6
(`tests/**/OpenAiCompat*Tests.cs`), where seven tasks legitimately write concrete files under one open
glob — a correct row a containment rule would call broken.

**The verdict is per ROW, against ONE task.** Let `A` be the row's resolvable candidates. The row is
**clean** when some single task `T` arm-matches **every** candidate in `A`. Otherwise one of **two
diagnostics** fires — and they are separate codes, not two messages under one code:

| Code | Name | Condition | What it means |
|---|---|---|---|
| **GR2068** | `HandoffPathUnreachable` | some `C ∈ A` is matched by **no task at all** | **provably broken.** Nothing in the plan can write that path, so the row cannot be delivered under any implementation |
| **GR2069** | `HandoffRowSplitAcrossTasks` | every `C ∈ A` is reachable, but no single task reaches them all | **confirm.** The row is delivered by several tasks; each half must be reachable by the task that implements *that* half, and only the author can say whether it is |

> **Why two codes and not one code with two messages.** GR2069 fires on three of ten rows of a plan that
> is **correct** (§4.6), and GR2068 fires once, on a genuine defect. Under a single code a reviewer learns
> to skim the code itself — and the precise half dies with the noisy half. That is the #229 muting
> failure, and it happens long before anyone reaches a "revisit if it gets noisy" tripwire. Two codes let
> an operator who has decided their tables split legitimately silence **GR2069** while **GR2068 keeps
> meaning "provably broken" forever**. No message text, however well written, can offer that.

> **Epitaph for the union form, because the reasoning is worth keeping.** An earlier revision checked
> coverage against the **union** of every task's `writeScope` — "is this path writable by anyone?" It was
> retired because §4.6's hand-run showed it catches **neither** failure in #553. Row 7 passes because task
> 21 happens to hold `PlanLoader.cs` while the task owning that row's half does not; row 1's `tests/**` is
> satisfied by any one test file anywhere in the plan, so it passes even against the original broken
> breakdown. A deterministic check that provably cannot fail on the case it was built for is the #229
> muting failure shipped pre-muted — worse than no check, because it looks like coverage. **What the union
> form bought was zero false positives on a healthy plan, and §4.6 is honest that per-row gives that up.**

### 4.6 Hand-run against plan 28's §13 — the ten real rows

Evaluated over plan 28's **56** `writeScope` entries across 30 tasks. Not a projection — the rules of
§4.3–§4.5, run.

> **Read plan 28's own table before using this as an oracle.** Plan 28 §13 has **four** columns —
> `| # | Agent | filesTouched | Deliverable |` — and **no `writeScope` column**; the five-column shape
> with the pinned-`writeScope` column is **plan 31's own**, introduced here (§13). So a candidate's
> coverage in plan 28 is resolved against the `writeScope` arrays in
> `docs/plans/28-local-inference-runner/tasks/*/task.json`, not against a column in that document. The
> check itself never needed the extra column — it reads the plan folder for scopes and the document
> only for the table — but a reader reconstructing this hand-run from the wrong shape will not find it.

| Row | Code | Detail |
|---|---|---|
| 1 | silent | `00-land-the-required-role-seam` covers both `Prompts/PromptInvocation.cs` and `tests/**` |
| 2 | silent | covered by `05-implement-shared-json-extractor` |
| 3 | **GR2068** | `tests/Guardrails.Integration.Tests/FakeOpenAiServer.cs` is in no task's scope. **A real defect**: the file shipped at `…/OpenAiCompat/FakeOpenAiServer.cs` |
| 4 | **GR2069** | five `Prompts/…` / `Model/…` files, all reachable, no single task holds all five |
| 5 | silent | covered by `17-implement-kind-aware-harness` |
| 6 | silent | covered by `09-add-openai-block-config-surface` |
| 7 | **GR2069** | the four `Loading/…` files, every one reachable, but no single task holds all four: `21-implement-reachability-gate` holds **three** (`PlanLoader.cs`, `PlanValidator.cs`, `DiagnosticCodes.cs`) and lacks `RawManifests.cs`, which only `09-add-openai-block-config-surface` writes |
| 8 | silent | `Cli/Commands/**` **dropped as unresolvable** (§4.4); the remaining `Model/PromptRunnerConfig.cs` is covered |
| 9 | silent | `Journal/**` resolves via the `**/` arm; `JournalTierSpend.cs` via the suffix arm |
| 10 | **GR2069** | two `docs/…` and two `.claude/…` paths, reachable, split across tasks |

**GR2068 fires once, on a real defect. GR2069 fires three times, all legitimate splits.** That is the
split's whole justification, measured rather than asserted.

**And against the ORIGINAL broken breakdowns — the acceptance criterion for this whole diagnostic:**

| Case | Result |
|---|---|
| Row 1, its owning task without `tests/**` (run 1 — $13.33, 19 blocked) | **GR2069** ✅ |
| Row 7, task 09 without `PlanLoader.cs` (run 3 — $3.84, 21 blocked) | **GR2069** ✅ |
| Row 1 against the **fixed** folder | silent ✅ |

> **Both catches land on GR2069, and that must not be glossed.** Neither plan-28 failure is a GR2068:
> in run 1 `tests/**` was reachable by the test-authoring tasks, and in run 3 `PlanLoader.cs` was
> reachable by task 21 — in both cases the row was *reachable across the plan* and unreachable by the
> **one task that owned it**. That is the split condition, exactly.
>
> **The consequence, stated so nobody discovers it later:** GR2068 is the precise, trustworthy code, but
> it catches **neither** failure #553 was written about. **GR2069 carries all of #553's motivating
> value** — so an operator who silences GR2069 as noise silences the entire reason this check exists,
> and keeps only a stale-path lint. That is an argument for making GR2069's message earn attention
> (§4.7), not for merging the codes back: merged, the muting takes GR2068 with it.

**The honest count, which is worse than the 2-catches / 2-false-positives figure quoted when this form was
proposed.** On the **fixed, shipped, correct** plan 28 the check fires on **four of ten rows**: one
genuine defect (row 3) and **three split-confirmations** (rows 4, 7, 10). Row 7 was counted earlier as a
catch; post-fix it is a legitimate split and fires anyway, because the check cannot distinguish a
deliberate split from a broken one — the same undecidability §4.8 records, seen from the other side. So
the real trade is:

> **2 catches on the broken folder; 1 genuine defect and 3 split-confirmations on the correct one.**

A 30% fire rate on a healthy plan is a genuine muting risk, and it is not engineered away — §4.7's message
is the mitigation and §11 Risk 1 records what to do if it proves insufficient. It is reported rather than
tuned: **no further narrowing exists that drops rows 4 and 10 without also dropping row 7**, because all
three are structurally identical — a row whose paths are reachable only across several tasks.

### 4.7 Severity, per code — and two messages that no longer have to share a voice

**Both ship as WARNING in v1**, for one reason: `RunCommand.cs:198-207` refuses to run a plan whose
validation emits any error, so an ERROR would be a **retroactive, run-blocking gate on every plan
carrying the convention** — and row 3 proves a correct, shipped, fully green plan can carry a stale cell.
A plan that ran last week would refuse to resume. WARNING is not toothless: `/guardrails-review` must
acknowledge or resolve a structural warning (the `GR2042` precedent, §3.4), and both diagnostics name the
row.

**GR2068's promotion criterion — specific, and now reachable.** Splitting the codes makes GR2068 a
provable impossibility rather than a judgement call, which is exactly the argument for ERROR. It becomes
an ERROR when a hand-run of **the unreachable form alone** across every plan in `docs/plans/` carrying a
`filesTouched` column produces only genuine defects. Today that is one fire, on one plan, and it is
genuine — promising, and one plan is not a corpus.

**GR2069 should probably never be an ERROR**, and this is not a "not yet". It reports a shape the check
cannot adjudicate — a deliberate split and a broken one are indistinguishable to it (§4.8) — so an
ERROR would refuse to run a plan whose author had already made the right call. It is a confirm, and a
confirm that blocks is a defect.

**The two forms no longer share a voice**, which is the second thing the split buys. GR2068's text gets
to be blunt; GR2069's carries the confirm framing without dragging GR2068 down to it.

```
WARNING GR2069 [28-local-inference-runner] handoff row 7 ("The block schema, the frontmatter fold,
  GR2065-GR2067, kind-aware GR2009"): every path this row names is writable by some task, but no
  SINGLE task can write all four.
      Loading/PlanLoader.cs       -> 09-add-openai-block-config-surface, 21-implement-reachability-gate
      Loading/RawManifests.cs     -> 09-add-openai-block-config-surface
      Loading/PlanValidator.cs    -> 19-implement-block-diagnostics, 21-implement-reachability-gate
      Loading/DiagnosticCodes.cs  -> 19-implement-block-diagnostics, 21-implement-reachability-gate
  A row deliberately split across tasks WILL trigger this, and that is expected - this is a CONFIRM,
  not a finding of fault. What to check: each half of this row must be reachable by the task that
  implements THAT half. Plan 28 halted twice on exactly this shape - a task told to deliver an
  outcome its writeScope could not reach - and the row read fine at plan level both times.
```

```
WARNING GR2068 [28-local-inference-runner] handoff row 3 ("The adversarial loopback server"): no
  task's writeScope contains 'tests/Guardrails.Integration.Tests/FakeOpenAiServer.cs'. No task in this
  plan can write it, so this row cannot be delivered under any implementation. Either the path is
  stale, or no task owns the deliverable. (This is not GR2069 - it is not a split.)
```

GR2069 names **which task** covers each path, because that is the fact the author needs and the check has
already computed it. GR2068 deliberately does **not** guess at a near-miss path: a suggested correction
that is wrong is worse than none.

### 4.8 What it still does not catch

- **A task whose guardrail needs a file the plan never named at all.** Both codes are decidable from the plan
  folder **plus the plan document**, and only for surfaces the plan declared. That residual gap is real
  and this plan does not close it.
- **A deliberate split, told apart from a broken one.** The check reports the shape and asks the author to
  confirm; it cannot adjudicate, because the table carries no per-row task attribution and cannot — it is
  authored by the architect **before** `/plan-breakdown` mints task ids. This is the same undecidability
  that makes three of four fires on a healthy plan a confirm rather than a defect.

### 4.9 Regression pins

1. **The row-7 catch — GR2069** (real, run 3). A row naming four files where **no single task holds all
   four** ⇒ **GR2069**, naming each path and the task(s) that cover it. In the real plan-28 folder the
   nearest task, `21-implement-reachability-gate`, holds **three** of the four and lacks
   `RawManifests.cs` (which only `09-add-openai-block-config-surface` writes); the fixture only needs to
   reproduce *some* such shortfall, not that exact 3-of-4 split. Asserting `GR2068` here is the
   mis-keying this pin exists to catch: every one of the four paths **is** writable by some task, so the
   row was never unreachable.
2. **The row-1 catch, both directions — GR2069** (real, run 1). A row naming a concrete path and
   `tests/**`, where no single task holds both ⇒ **GR2069**; add `tests/**` to that task's `writeScope`
   ⇒ **silent**. The second half is what proves the check measures coverage rather than merely counting paths.
3. **The unreachable case — GR2068** (plan 28 row 3, real). A cell naming a concrete file no `writeScope`
   entry matches, while a same-named file exists at a different path ⇒ **GR2068**, with no suggested
   correction.
3a. **The codes are mutually exclusive per row.** A fixture row that would satisfy both conditions is
   impossible by construction — an unreachable path means no single task covers the row either — so the
   pin asserts that a row emitting GR2068 emits **no** GR2069, on the same diagnostic list. Without it,
   an implementation that emits both for every broken row makes silencing GR2069 useless.
4. **The anchor discriminator — both halves in one fixture.** A cell containing both `tests/…/Wrong.cs`
   (anchored, unmatched) and `Cli/Commands/` (unanchored) ⇒ exactly **one** finding, a GR2068 for the
   first. Without the negative half, a later "improvement" that drops the anchor test passes every other
   pin here and re-introduces row 8's noise.
5. **The argument-direction pins.**
   (a) A glob candidate covered by a concrete scope entry must be **silent**, and the pin must **fail**
   under the un-swapped form `IsInScope(C, U)` — that form can never match a glob, so every glob row would
   fire and a test passing both ways proves nothing.
   (b) A concrete relative candidate must be covered by the **suffix** arm and not by substring matching:
   a scope entry of `src/Foo/BarPlanLoader.cs` must **not** cover a candidate of `PlanLoader.cs`.
6. **The SILENCE pin.** A plan with **no handoff table** emits nothing at all — asserted on the **full
   diagnostic list being unchanged**, not on the absence of either code.
7. **The prose-cell pin.** A cell containing only backticked non-paths (`required`, `writeScope`) emits
   nothing.
8. **The no-second-matcher pin** (§13 stage 5's guardrail, not a unit test). A guardrail greps the new
   file for the literal `WriteScope.IsInScope(` and **fails on any local segment-glob logic** — a
   `Split('/')` paired with `'*'` handling inside `HandoffScopeCoverage.cs`. Without it, a private inline
   matcher that happens to agree with every fixture above passes all of them and silently owns a second
   copy of the glob grammar.

---

## 5. #545 part 3 — warn when a plan folder is edited during a live run

Parts 1 and 2 shipped in `1952c9b` (`RunCommand.cs:1124-1216`): the three-way `[y] / [a] / [N]` prompt
with each branch's cost stated, and the actionable decline that says a restore must be byte-exact. **Part
3 is the whole of the remaining scope**, quoted from the issue:

> **Warn when a plan folder is edited during a live run** — the actual root cause here.

All seven of plan 28's escalation fixes were plan-folder edits, each hand-sequenced after the run exited
specifically to dodge this hazard. A warning makes that discipline the harness's job rather than the
operator's memory.

### 5.1 The fact the issue does not state: the plan folder is only *partially* live

An edit made during a run does not simply "not apply". The harness reads some definition inputs fresh and
holds others from load:

| Input | Read when | A mid-run edit… |
|---|---|---|
| An action prompt file | per attempt — `ActionRunner.cs:107` `LoadPromptFile(task.Action.Path)` | **applies** to the next attempt of that task |
| A guardrail script | per guardrail run, from disk | **applies** |
| `task.json` (`writeScope`, `dependsOn`, retries, `maxTurns`) and the DAG | once, at plan load | does **not** apply to this run |
| The recorded `definitionHash` | at settle — `AttemptJournaler.cs:90`, `TaskExecutor.cs:590` call `TaskDefinitionHash.Compute(task)` over **current disk bytes** | records the **post-edit** hash |

The third and fourth rows together are a quiet false green: a task edited mid-run can run the old
`task.json` semantics, succeed, and record the new hash — after which the next resume compares equal and
never flags it. **This plan does not fix that** (§10); it is why the warning's text must state what an
edit does and does not reach, rather than saying "your edit was ignored", which is false.

### 5.2 Where the check fires, and the seam it exposes

A new `src/Guardrails.Core/Execution/LivePlanEditWatch.cs`. **Its public signature is pinned here**, so
the stub stage, the test stage and the implementation stage cannot disagree about it — the pair the
adversarial pass showed would otherwise deadlock into needs-human:

```csharp
public sealed record PlanEditedFile(string TaskId, string Label, PlanEditKind Kind);
public enum PlanEditKind { Added, Removed, Modified }

public sealed record PlanEdit(string TaskId, string OldHash, string NewHash,
                              IReadOnlyList<PlanEditedFile> Files);

public sealed class LivePlanEditWatch
{
    public LivePlanEditWatch(PlanDefinition plan);

    /// <summary>Recompute the definition surface, return what changed since the last call, and
    /// re-baseline. Empty when nothing changed. Never throws: an unreadable file is skipped.</summary>
    public IReadOnlyList<PlanEdit> Poll();

    /// <summary>Silently re-baseline these tasks — a HARNESS-authored edit is not an operator edit.
    /// An unknown task id is a no-op. Pass no ids to re-baseline the whole plan.</summary>
    public void Rebaseline(params string[] taskIds);
}
```

The baseline is, per task, the per-**file** hashes of `TaskDefinitionFiles.Enumerate(task)` (`task.json`,
the resolved action file, `guardrails/**`, `preflights/**`), computed with the same `HashText` primitive
`TaskDefinitionHash` uses, so the two cannot disagree about what defines a task. `logs/` and `state/` are
not in that enumeration, which is why the harness's own constant writes into the plan folder cannot
trigger this.

**One deliberate divergence from the hash: an ignore list, applied HERE and not in `HashText`.**
`HashText.EnumerateFolderFiles` (`:54-72`) enumerates `"*"` with `SearchOption.AllDirectories` and filters
**nothing**, so a stray `.DS_Store`, `Thumbs.db`, `*.swp`, `*.orig` or `*.rej` in a `guardrails/` folder is
part of a task's definition. The watch drops those patterns before comparing. It must **not** be fixed in
`HashText`: that function feeds `TaskDefinitionHash` and `PlanDefinitionHash`, so changing its file set
silently changes every recorded definition hash — and a changed definition hash is a **definition-drift
halt** on the next resume of every affected plan. Applying the ignore list only in the watch makes the
watch strictly **quieter** than the hash and never noisier; anything the hash sees and the watch ignores
is a pre-existing drift condition the resume-time check already owns. *(Whether `HashText` should carry
the list is a real question with a migration attached — filed, not answered here.)*

`Poll()` is called by the **Scheduler**, on the scheduler's own thread, at two boundaries that already
exist: **task dispatch** and **task settle**. No new thread, no lock, no daemon (invariant 6).

**No `FileSystemWatcher`** — it would fire on the harness's own writes under the plan folder, needs a
debounce policy, and is platform-quirky. Polling costs at most `2N` recomputes of the definition surface
per run (a few hundred KB of reads against a run that spends dollars per attempt); the choice costs
timeliness: **the warning appears at the next scheduler boundary, not instantly**, and a single long task
retrying alone can delay it by one attempt. Per-file baselines mean the report names **which files**
changed with no git involvement — strictly more robust than the resume-time `DefinitionDriftReporter`,
whose per-file breakdown is best-effort on recovering prior bytes from a commit.

### 5.3 What must NOT fire it — and the answer is not what it looks like

An advisory that fires on the harness's own writes stops being read (#229). An earlier revision of this
section named three exclusions. **All three were wrong**, and the correction changes the deliverable:

- **The overwatcher is NOT a mid-run definition writer.** `Overwatch.cs:162-171` extracts only the two
  `Allowlist` levers — `GuidanceInjection` and `BudgetOverride` — and `Decide` (`:296-355`) gates on
  `hasSanctionedChange` and returns an **in-memory** `OverwatchDecision`. There is no file write anywhere
  in the apply path. `FileEdit` / `TaskFieldEdit` / `Denylist` are parsed and classified but have **no
  apply path in v1** (`OverwatchFixClassifier.cs:172-173`: *"v1-inert — there is no apply path"*). So the
  exclusion was dead code, and the negative pin written against it tested an **unreachable state** — it
  would have passed with the whole feature absent, which is precisely the archetype this plan exists to
  hunt.
- **The real mid-run definition writer is JIT wave breakdown, and it has plan-wide authority.**
  `WaveBreakdownInvoker.cs:129-137` runs a Claude subprocess with `workingDirectory: plan.PlanDirectory`
  (the *plan* folder, not the wave folder), `PermissionMode = "acceptEdits"`, `AllowedTools` including
  `Write`, `Edit` and `Bash`, and — grep the file — **no containment hook, no `writeScope`, no
  path constraint of any kind**. It can rewrite any other wave's `tasks/`, any task's `guardrails/`, or
  `guardrails.json`. `BreakdownInventory` inventories only the *current* wave, so it is not a bound on
  what the invoker may touch.
- **Three destructive harness operations move or delete definition files mid-run.**
  `BreakdownInventory.Revert` (`:175-233`) moves attempt-created files to `rejected/` and restores
  pre-existing ones from snapshot; `SweepIncompleteTrailingTaskFolders` (`:136-160`) moves incomplete task
  folders to `rejected/tasks/`; and `Scheduler.QuarantineWholeTasksFolder` (`:1946-1985`) moves a wave's
  **entire `tasks/` directory** to `rejected/tasks`, with a catch branch that hard-deletes it recursively
  (`:1973`).
- **Drift resolution is NOT pre-DAG on a waved plan.** `TryResolveDrift` (`Scheduler.cs:2408`) has one
  call site — `Scheduler.cs:621`, inside `DrainAsync`, which the wave loop calls **once per wave**
  (`:517`). Its destructive section (`git reset --hard`) therefore fires mid-run.

**The rule that follows:** the Scheduler calls `Rebaseline()` — **plan-wide, no task ids** — after each
of: a JIT wave breakdown attempt, `BreakdownInventory.Revert`, `SweepIncompleteTrailingTaskFolders`,
`QuarantineWholeTasksFolder`, and a `TryResolveDrift` that resolved. Plan-wide, not per-task, because
three of the five have authority over files outside the unit they nominally act on, so a per-task
re-baseline would leave the watch reporting the harness's own writes as operator edits. Tasks absent from
the baseline (a JIT wave's new tasks) are added silently on the next `Poll()`.

**Say what this is: a workaround for #557, not a fix.** Re-baselining plan-wide is only necessary because
`WaveBreakdownInvoker` has plan-wide write authority it should not have — a Claude subprocess with
`Write`/`Edit`/`Bash` at `acceptEdits`, rooted at the plan directory, with no containment hook and no
`writeScope`. **#557** tracks scoping that authority to the wave it is authoring. Until it lands, the
watch pays for the invoker's reach by going blind to any operator edit that lands in the same window as a
JIT breakdown — a real, accepted hole in this feature, caused by a hole in a different one.

The watch reports **human** edits. That is the entire value, and getting these exclusions wrong is the way
the feature dies.

### 5.4 What it reports — through the observer that already ships

**Severity: a WARNING that never halts.** Halting would destroy the exact workflow the maintainer used all
night — fixing a defective guardrail while the rest of the DAG runs. It is also not, for the in-flight
task, a correctness problem: prompts and guardrails are re-read per attempt.

**No new `IRunObserver` event.** `DecisionRecorded(DecisionEntry)` already exists (`IRunObserver.cs:131`),
is rendered by **both** operator surfaces — `ConsoleRunObserver.cs:123` for the `--no-ui` stream and
`LiveRunObserver.cs:532` for the live table — and is forwarded by **both** transparent decorators
(`OnTheFlyLogSiteObserver.cs:194`, `OnTheFlyDiagramObserver.cs:203`). A new event would have to be added
to all five, and `IRunObserver`'s members carry **default no-op bodies**: a decorator missed in the wiring
would compile, pass every test that does not exercise it, and drop the warning silently — this plan's own
failure archetype, shipped inside the fix for it. Reusing `DecisionRecorded` buys four rendering sites and
two decorators through code that already ships and is already tested.

The three surfaces are then:

1. **Live and `--no-ui`** — `_observer.DecisionRecorded(entry)`; both renderers already handle it.
2. **Durable** — the same entry appended to `decisions[]` in `run.json` (SSOT §7).
3. **Terminal** — the end-of-run report (§13 stage 8).

**The entry's shape, and why it does not reuse `boundary: "drift"`.** A consumer filtering on
`boundary == "drift"` would start counting observations as drift decisions — the drift boundary means *a
gate was reached and resolved*, and nothing here was resolved. So:

| Field | Value |
|---|---|
| `boundary` | **`plan-edit`** — a new token, additive alongside `drift` / `wave` / `task` |
| `decision` | **`observed`** — a new token: *the harness noticed and reported; nothing was decided and nothing changed* |
| `policy` | the run's `autonomyPolicy` in force, like every other entry |
| `subject` | the edited task ids, comma-joined (the `drift` entry's own convention) |
| `headline` | **REQUIRED** — a one-line summary, like every other entry. `DecisionEntry` declares `Boundary`, `Policy`, `Decision`, `Subject` **and `Headline`** all `required`, so an entry built from the other five rows alone does **not compile** (CS9035). An earlier revision of this table omitted it |
| `detail` | the per-file added / removed / modified list |

A `PlanEditDecisions.Observed(...)` factory beside the existing `DriftDecisions` factories in that same
file is the natural home for the construction, and is what keeps the required fields from being
rediscovered at each call site.

**Both new tokens are outcome-inert, and the reason is the `decision` token, not the boundary.**
`RunOutcomePolicy.cs:33-46` is the only consumer that branches on a decision, via two predicates —
`SuppressesDelivery` (`decision == proceeded-best-guess || proceeded-unreviewed`) and
`ProceededUnreviewedWaveCount` (`decision == proceeded-unreviewed`). Neither reads `Boundary` at all
(grep: zero hits). `observed` is neither token, so a `plan-edit` entry cannot suppress `mergeOnSuccess`
(`Scheduler.cs:750`) and cannot reach `ExitCodes.ProceededUnreviewed` (`RunCommand.cs:951-953`). The
inertness claim therefore holds for the new boundary — but it is a fact about the **decision** token, and
a future token that is not `observed` would need re-checking.

**`RunReport.Decision` is singular** (`RunReport.cs:310`, `DecisionEntry?`), and it means *the pre-DAG
drift decision this run took*. A run can produce **N** plan-edit observations. Rather than widen that
field — which would touch the shipped drift renderer for a reason unrelated to drift — the report gains a
sibling:

```csharp
public IReadOnlyList<DecisionEntry> Observations { get; init; } = [];
```

Additive, defaulted, so no existing consumer changes. The split is meaningful rather than convenient:
`Decision` is something the harness **decided**, `Observations` are things it **noticed** — which is
exactly the distinction the `observed` token names.

The rendered text must state all three consequences from §5.1 and overstate none:

```
PLAN FOLDER EDITED DURING THIS RUN (SSOT 7.2) - 1 task's definition changed since the run started.
  28-record-openai-compat-in-ssot: sha256:9a41c7. -> sha256:0e2b88.
    modified  guardrails/02-ssot-carries-keys.sh  (+3 -1)
  Nothing was halted and nothing was re-run.
  What your edit reaches: this task's action prompt and guardrail scripts are re-read on every
    attempt, so an edit to either applies from the next attempt onward.
  What it does NOT reach: task.json (writeScope, dependsOn, retries, maxTurns) and the DAG were
    loaded when this run started; edits to those apply only to a later run.
  This task will record the POST-edit definition hash when it settles, so a later resume will not
    flag this as drift.
```

### 5.5 Regression pins

1. **The positive pin.** A run in which a task's `guardrails/*.ps1` is modified after the run starts and
   before that task settles emits exactly one `DecisionRecorded` call and exactly one `decisions[]` entry
   with `boundary: "plan-edit"`, `decision: "observed"`, naming that task and that file.
2. **The negative pin — a JIT wave breakdown produces none.** A waved fixture whose wave-2 breakdown
   authors task folders, and whose `BreakdownInventory.Revert` then rejects them, must emit **zero**
   `plan-edit` entries. This replaces an earlier pin written against an overwatcher fix, which §5.3 shows
   cannot edit a definition in v1 — that pin tested an unreachable state and would have passed with the
   feature entirely absent.
3. **The outcome-inertness pin.** A run carrying a `plan-edit` observation and nothing else still
   fast-forwards on success (`SuppressesDelivery` false) and still exits `0`, not `5`. Asserted on the
   exit code and the delivery record, not on the predicate.
4. **The ignore-list pin.** Creating a `.DS_Store` under a task's `guardrails/` mid-run emits nothing —
   and the same run's recorded `TaskDefinitionHash` still **changes**, proving the watch is quieter than
   the hash by design rather than by accident (§5.2).

---

## 6. Why #553 does not subsume #554 — read this before deprioritising either

These two land in the same triage round and look adjacent. The wrong conclusion is easy to reach: *"once
GR2068 stops unreachable write scopes, escalations mostly go away, so preserving their work matters
less."* One night of plan 28 refutes it.

Of the escalations plan 28 produced across runs 3–9, four were `writeScope`-shaped and **two were
categorically outside anything GR2068 could ever catch**:

- **Task 05 — a design contradiction.** `OverwatchProposalFenceTests.Unfenced_Prose_StaysNull`, authored
  hours earlier for #551, asserted the exact opposite of plan 28 §3.3's acceptance. Both files were in
  scope; two correct-looking requirements contradicted each other and only a human could pick a winner.
- **Task 28 — a self-refuting guardrail.** A class-wide test filter, an unconditional `exit 1`, and a
  printed pass case inside a failure branch it could never reach — deadlocked against task 29, which
  `dependsOn` it. No path was missing; the check was wrong.

Both codes validate those two plans clean, correctly. Between them they catch **two** of the four
`writeScope`-shaped instances (§4.6: rows 1 and 7 against their broken breakdowns) — not four, and both
land on **GR2069**, not GR2068. The frequency argument for #553 has to be read at that size.

**The division of labour, stated so it cannot be conflated:**

- **#553 reduces the FREQUENCY** of escalations, by catching one cause before a dollar is spent.
- **#554 reduces the UNIT COST** of every escalation that still happens.

They are independent to build — #553 lives in `PlanValidator`, #554 in `TaskExecutor`. Escalations are a
permanent and **desirable** feature: the needs-human gate is how an agent refuses to guess, and every one
of plan 28's seven was correct to fire. With #554 fixed an escalation stops being a $10 event and becomes
a pause, which makes the harness far more tolerant of the legitimate halts you want it making. A cheap
escalation is one nobody is tempted to design around.

---

## 7. How this is tested

- **#554** needs the **integration** suite for its core pins: the salvage path is worktree-only
  (`IsRealGitSegment`), so a Core-only test with the fake worktree provider (`TaskBase = "0000…"`) passes
  with the feature entirely absent. The pins assert on a real git segment, on the **patch file's bytes**,
  the **ref's existence**, and the **composed prompt** of the following attempt.
  **No stub stage is needed for #554**, and that is a deliberate constraint on the tests rather than an
  accident: every pin is written against an **observable artifact** (a file, a ref, a composed string)
  and none names `SalvageFraming` or `PriorAttemptRef.SalvagePatchPath`, so the test stage compiles
  against today's assemblies and fails for the right reason.
- **GR2068 / GR2069** are a pure structural check over `PlanDefinition` plus a sibling `.md`,
  Core-testable with fixture plan folders built in temp dirs. The fixtures assert on the **code literals**
  `"GR2068"` / `"GR2069"`, not on the `DiagnosticCodes` constants, for the same compile reason; a pin in
  the implementation stage asserts each constant equals its literal. Two fixtures are the **real** plan-28
  rows in their broken state (§4.9 pins 1 and 2) — the acceptance criterion, not invented shapes — and
  both assert **GR2069**, which is the mis-keying most likely to slip through.
- **The plan-edit watch DOES need a stub stage**, because its tests must construct `LivePlanEditWatch`
  and there is no way to write them against an observable artifact alone. §13 stage 6 writes the type
  declared-and-inert; §5.2 pins its exact public signature so the stub, the tests and the implementation
  cannot disagree.
- **No test in this plan may be satisfied by a fake that also passes with the feature removed.** That is
  the #382 lesson and it applies to all three.

---

## 8. Done when

Each bullet closes a specific wrong-but-passing implementation.

**#554**

- A task whose action emits `needsHuman` **after writing files** leaves a non-empty `prior-attempt.patch`
  and a ref at `refs/guardrails/<taskId>/attempt-<N>` — **asserted on a real git segment**, because the
  fake provider makes the whole path a no-op.
- An **out-of-scope** write by that attempt is **absent** from the patch and from the ref's tree (§3.4
  divergence 3). Without this, salvage becomes a durable, agent-readable channel for exactly the edits
  the write-scope check exists to strip.
- The following attempt's composed prompt carries the **recovery routing** — the size-routed choice
  (`prior-attempt.patch` first, then `git show "<ref>:<path>"`) and the `writeScope` caveat — asserted on
  the composed bytes, not on `feedback.md`, and not satisfiable by one more path bullet.
- The **escalation `Context`** a human or firstmate reads at the halt names the ref and the patch.
- A `needsHuman` on a **final** attempt still preserves — catches an implementation that copied
  `StashIfRollingBack` verbatim.
- A `needsHuman` attempt that wrote **nothing in scope** leaves no patch, no ref, and no salvage section.
- **The two shipped salvage suites pass with zero edits** — `RetryPolicySalvageAdviceTests` and
  `RetrySalvageTests` — proving the `Retry` framing's bytes did not move. This is what makes stages 2 and
  3 legitimately test-free.
- Salvage refs for one task never exceed `SalvageRefRetentionPerTask`, asserted across simulated repeat
  escalations.
- Serial mode is byte-identical to today on the escalation path.

**#553**

- **The two real catches fire, and fire as `GR2069`** (§4.9 pins 1 and 2): plan 28 row 7 with task 09
  lacking `PlanLoader.cs`, and row 1 with its owning task lacking `tests/**`. These are the acceptance
  criterion — an implementation that passes every other bullet here and misses these two is the check
  #553 asked for, not built. **Neither is a GR2068**, and a pin asserting GR2068 for either is wrong.
- **The codes are mutually exclusive per row** (§4.9 pin 3a): a row emitting GR2068 emits no GR2069.
  Without it, an implementation that emits both for every broken row makes silencing GR2069 useless.
- **Row 1 goes SILENT once `tests/**` is added to that task** — the half that proves the check measures
  coverage rather than counting paths.
- The two message forms are **distinct**: `unreachable` names no covering task; `split` names, per path,
  the task that covers it, and says in its own words that a deliberate split is expected to trigger it.
- The **anchor discriminator** holds both ways in one fixture (§4.9 pin 4): an anchored-but-unmatched cell
  fires, an unanchored fragment is silent, and exactly one finding is emitted.
- `validate` emits **nothing at all** for §4.9 pins 6 and 7 — asserted on the **full diagnostic list**.
- The glob arm's arguments are **swapped** (§4.9 pin 5a): the fixture must FAIL under `IsInScope(C, U)`.
- The concrete arm matches on a **segment-aligned suffix**, not a substring (§4.9 pin 5b).
- A guardrail greps `HandoffScopeCoverage.cs` for the literal `WriteScope.IsInScope(` and fails on local
  segment-glob logic (§4.9 pin 8). No second copy of the glob grammar.
- `DiagnosticCodes.HandoffPathUnreachable == "GR2068"` and `HandoffRowSplitAcrossTasks == "GR2069"`, the
  next-free marker reads **GR2070**, and the three reserved-by-name gaps (GR2060, GR2061, GR2054) are
  untouched.

**#545 part 3**

- A mid-run edit to a task's guardrail script produces exactly one `DecisionRecorded` call and one
  `decisions[]` entry with `boundary: "plan-edit"`, `decision: "observed"`, naming the file.
- **A JIT wave breakdown produces none** — the negative pin, rewritten against a reachable state (§5.5
  pin 2). The earlier overwatcher-fix pin is deleted: `Overwatch.cs:162-171` cannot edit a definition in
  v1, so that pin passed with the feature absent.
- A run carrying only a `plan-edit` observation still fast-forwards and still exits `0`, not `5`.
- A stray `.DS_Store` under a task's `guardrails/` emits nothing, while the same run's recorded
  `TaskDefinitionHash` still changes (§5.2).
- The rendered text carries all three §5.1 consequences — asserted on the string, because this is the one
  place a half-true message actively misleads.

**Both**

- SSOT §3.2, §7, §7.2, §8, §9 and §9.6 carry every change (§12), and `guardrails-domain-knowledge` is
  updated in the same change (invariant 4).

---

## 9. Running this plan unattended

Executed by the harness with `--autonomous --max-cost-usd <cap> --no-merge-on-success`.

**What an unattended run of this plan must not be allowed to do.** Every deliverable here is a *check* —
a salvage guarantee, a validate diagnostic, a mid-run warning. The cheapest wrong implementation of a
check is always to weaken the thing that would have caught its absence:

- **No implementation task writes under `tests/**`, and no task holds a blanket test glob.** §13 pins
  every `writeScope` verbatim as concrete file paths. That the implementation stages need no test paths
  is **verified, not assumed**: `AttemptJournaler.NeedsHuman` has one caller; `PriorAttemptRef`'s new
  members are optional; `SalvageFraming` is a defaulted parameter so the two shipped salvage suites do not
  move; the GR2068/GR2069 fixtures assert on the code literals. If any of those turns out to be false at
  breakdown, the fix is to move the test edit into that milestone's **test** stage — never to widen an
  implementation stage.
- Every implementation stage carries a **`tests-untouched` protected-artifact guardrail** (SSOT §3.4).
  Note what it does **not** do here: on the escalation path the salvage-suppression that normally backs it
  is structurally inapplicable (§3.4), which is why the `writeScope` filter is a deliverable rather than a
  nicety.
- **No task may narrow an assertion, delete a fixture, or relax a guardrail to reach green.** The
  deterministic per-attempt re-check is the load-bearing guarantee.
- **The silence pins are the ones most at risk.** A pin asserting "GR2068 does not fire" passes trivially
  when GR2068 is broken; §4.9 pins 6 and 7 therefore assert on the **entire diagnostic list**, and stage 4
  owns making that true — as do pins 1 and 2, which are the two REAL plan-28 rows in their broken state. Likewise §5.5 pin 2 — a negative pin must test a **reachable** state, which is
  exactly what the deleted overwatcher pin did not.
- **`--no-merge-on-success` means a green run does not deliver.** The plan branch must be merged by hand,
  and the loud post-summary banner (#340/#542) says so. Read to the end of the output and check
  `git branch --no-merged master` before claiming this shipped.

**Sizing.** Ten stages — three test-authoring, one stub, five implementation, one documentation. Two new
source files, one new diagnostic code, one new decision token, one new boundary token. No stage touches
more than five files. Nothing needs model tiering, local inference, or network access.

---

## 10. Out of scope

- **Recording the definition hash a task actually RAN under — filed as #556.** §5.1's quiet false green: a
  task edited mid-run records the post-edit hash at settle, so no later resume flags it. The fix (capture
  each task's hash at load and journal *that*) is small but it changes §7.2's drift semantics, and
  shipping a drift-contract change inside a three-issue hardening plan is how a contract change goes
  unreviewed. This plan **warns** about it (§5.4); #556 fixes it.
- **An ignore list on `HashText.EnumerateFolderFiles`** (§5.2). Changing it moves every recorded
  definition hash, and a moved definition hash is a drift halt on the next resume of every affected plan.
  The watch carries the list locally, where it is strictly quieter than the hash and never noisier.
  `HashText` itself is **not touched by this plan**; the question and its migration are §14's one open
  item.
- **A GR2068 / GR2069 severity flag or config knob** (§4.7). Severity is one factory call per code.
- **A plan-document path fallback for either code** when the sibling `.md` is absent (§4.2).
- **A `FileSystemWatcher` or any instant edit detection** (§5.2).
- **Halting on a mid-run plan-folder edit** (§5.4).
- **Constraining `WaveBreakdownInvoker`'s plan-wide write authority — filed as #557.** The watch works
  *around* it by re-baselining plan-wide (§5.3), which is a **workaround, not a fix**: the authority is
  unbounded by design today and narrowing it is a containment change with its own blast radius. It is a
  bigger hole than anything this plan closes.
- **Any change to `validate`'s static-and-offline contract.** Both codes read the sibling `.md` and the plan
  folder; it opens no socket and probes no executable.
- **Salvage on the `permission-denied` / `task-preflight-failed` short-circuits.** Same shape as #554 and
  plausibly the same fix, but neither was observed costing anything on plan 28. YAGNI until measured.

---

## 11. Risks accepted

**The shared risk, first.** All three changes add a surface that could become noise, and the rule they are
each designed around is that **a signal firing when nothing is wrong is worse than no signal** — it gets
muted, and then the real one is invisible too.

| Change | The always-fires trap | How it is closed |
|---|---|---|
| #554 salvage | A "recover your work" section for an empty patch | The existing empty-diff guard, unchanged (§3.4) |
| GR2068 / GR2069 | Firing on every legacy plan; firing on a relative or vague cell that is not actually broken; the noisy half muting the precise half | No table ⇒ silent; no sibling `.md` ⇒ silent; prose cells ⇒ silent; the suffix and `**/` arms resolve relative cells; the whole-segment anchor drops unresolvable ones (§4.2–§4.5); and the two conditions are **separate codes** so the noisy one can be silenced alone (§4.5). What remains is Risk 1 |
| Plan-edit watch | Firing on the harness's own JIT-breakdown and revert writes | Plan-wide re-baseline after each of the five harness writers (§5.3) |

**Risk 1 — GR2069 fires on three of ten rows of a plan that is CORRECT** (§4.6), and GR2069 is the code
carrying all of #553's motivating value. The split condition cannot distinguish a deliberate split from a
broken one, and the union form that had no false positives caught neither motivating failure. **Accepted,
with two mitigations, both shipped rather than deferred:** (a) the codes are **separate** (§4.5), so an
operator who decides their tables split legitimately silences GR2069 and GR2068 keeps meaning "provably
broken" — the muting cannot spread; (b) GR2069's message reads as a **confirm** and says in its own text
that a deliberate split is expected to trigger it (§4.7).

**The residual, named because the split creates it.** Silencing GR2069 silences **100% of what #553 was
written to catch** — both plan-28 failures are GR2069s (§4.6) — leaving only GR2068's stale-path lint.
That is the honest cost of making the noisy half silenceable, and it is still the better trade: under one
code the muting takes the precise half with it and nobody notices, whereas silencing GR2069 is a
deliberate, recorded act by an operator who has read what it does.

**Risk 2 — the `filesTouched` column becomes a contract rather than prose, and per-row makes it a
stronger one.** A row is now expected to be deliverable by **one** task, or to be written as several
rows. That is a real constraint on how architects write handoff tables, and it pushes toward
one-row-per-task — which is, not by accident, the shape §13 already uses. **Accepted**; at WARNING the
cost of being wrong is a line in a report, and §13's own table is the first adopter. No claim is made
about `plan-breakdown`'s golden example — that folder has no handoff table, so there is nothing to adopt.

**Risk 3 — the plan-edit warning can be late.** A single long task retrying alone delays it to the next
scheduler boundary. **Accepted**: the alternative is a watcher that fires on the harness's own writes.

**Risk 4 — the watch is quieter than the definition hash.** The §5.2 ignore list means an edit the hash
sees can pass the watch silently. **Accepted, and it is the correct direction**: that case is a
pre-existing drift condition the resume-time check already owns.

**Risk 5 — advertising a salvage ref that no longer exists.** Refs are pruned when a task's final settle
is `succeeded` (`Scheduler.cs:3483-3487`), and the retention cap (§3.4) drops the oldest. A prompt
composed after either could name a dead ref. **Accepted and bounded**: pruning happens only on success,
after which no further attempt of that task is composed; the cap is set above the retry budget; and the
patch file — the primary route — is never pruned.

**Risk 6 — the escalation salvage section changes what a resumed agent does.** An agent that previously
re-authored from scratch will now adopt prior work. Salvaged files remain fully subject to the task's
`writeScope` and to every guardrail re-running on the attempt's final state (SSOT §3.2). **Accepted** —
the #306 argument unchanged, now with the §3.4 filter in front of it.

**Risk 7 — `action.path` fan-out multiplies the watch's output.** `PlanLoader.cs:1109-1130` validates
`action.path` for **existence only** — no containment check, no uniqueness check — so N tasks may
legitimately share one action script. Editing that script mid-run reports **N** edited tasks, one per
sharer, which is literally correct and reads as noise. **Accepted, not engineered around**: de-duplicating
by file would hide which tasks are affected, which is the fact the operator needs. The renderer groups by
file when one file appears under several task ids.

---

## 12. Exact SSOT edits (`docs/plans/02-schemas-and-contracts.md`)

Invariant 4: these land in the same change as the code they describe.

1. **§3.2, "Scope — EVERY non-final worktree failure" (line ~738).** The scope sentence lists
   *"guardrail-fail, action-fail, timeout, max-turns, output-cap, and write-scope"*. Add the escalation
   path with its three divergences named (§3.4): salvage **also** fires on an action-emitted `needsHuman`,
   **regardless of `isFinal`**; the gate is "a real git segment", not "will be reset", because the
   escalating attempt's tree is **orphaned** (a resume creates a fresh segment at `planHead`, so nothing
   is ever handed back); and the staged set is **filtered to the task's `writeScope`**, because this path
   short-circuits ~250 lines upstream of the write-scope check and `ScopedRevert`. State that the
   protected-artifact suppression is **structurally inapplicable** here (`failed` is empty — no guardrail
   ran) and that the scope filter is what takes its place. State that the feedback wording on this path
   must not claim a rollback.

2. **§3.2, "Pruning" (line ~763).** Two clauses. *"A task that never succeeds (exhausts to `needs-human`)
   keeps its salvage refs until the next `--fresh`"* now also covers the action-emitted escalation, which
   previously produced no refs at all — and a **per-task retention cap** now bounds the growth that
   creates.

3. **§8, per-attempt log layout (line ~3240).** `prior-attempt.patch`'s comment reads *"retry salvage
   (§3.2, #306): applyable diff of THIS rolled-back attempt vs taskBase"*. Reword to admit the escalation
   case, which is **not** rolled back, and to say the escalation form is scope-filtered.

4. **§9, the `needsHuman` bullet (line ~3541).** *"the harness treats the attempt as needs-human
   immediately (no retry burn)"* gains: and preserves the attempt's **in-scope** work per §3.2, exposing
   the ref and patch in the escalation record and in the next attempt's composed prompt.

5. **§7, `decisions[]` (line ~2211).** Two additive tokens: **`boundary: "plan-edit"`** alongside
   `drift` / `wave` / `task`, and **`decision: "observed"`** — *the harness noticed and reported at this
   boundary; nothing was decided and nothing changed.* State the inertness precisely: `RunOutcomePolicy`
   branches on the **`decision`** token only and never reads `boundary`, so `observed` cannot suppress
   delivery or reach exit code 5 — and a future token that is not `observed` must be re-checked against
   `SuppressesDelivery` / `ProceededUnreviewedWaveCount`.

6. **§7.2 (line ~2798).** Three additions.
   (a) **"The plan folder is only partially live during a run"** — §5.1's table in prose: action prompts
   and guardrail scripts are re-read per attempt; `task.json` and the DAG are held from load; the recorded
   `definitionHash` is computed at settle from current disk bytes. Name the consequence — a
   mid-run-edited task records the post-edit hash and a later resume compares equal — as a **known
   limitation**, in the register of §7.2's two existing boundary calls, with the follow-on issue named.
   (b) **The watch**: where it polls, what it reports, and the five harness writers that re-baseline it
   plan-wide.
   (c) **A correction to the existing text.** §7.2 presents the drift gate as pre-DAG. On a **waved** plan
   it is not: `TryResolveDrift` is called from `DrainAsync` (`Scheduler.cs:621`), which the wave loop runs
   **once per wave** (`:517`), so the gate — including its `git reset --hard` — can fire mid-run. This is
   a pre-existing inaccuracy the watch's design surfaced; it is corrected here rather than left standing.

7. **§9.6's validation table (line ~4939).** One new row, after `GR2067`:

| Code | Sev | Rule |
|---|---|---|
| `GR2068` | warning | `HandoffPathUnreachable` — a handoff row names a resolvable path that **no task's** `writeScope` covers, so the row cannot be delivered under any implementation. Shared extraction (plan 31 §4, issue #553): candidates are backticked code spans in the plan document's implementation-handoff table carrying a `/` or a file extension; a candidate is **resolvable** only when its first path segment equals a **whole** path segment of some `writeScope` entry in the plan (so a vague fragment like `Cli/Commands/` — where the real segment is `Guardrails.Cli` — is dropped silently rather than reported). A **concrete** candidate is covered by `WriteScope.IsInScope(candidate, [entry])`, by equality, or by a **segment-aligned path suffix** of an entry; a **glob** candidate is covered when `IsInScope(entry, [candidate])` or `IsInScope(entry, ["**/" + candidate])` — **arguments swapped**, the only direction the primitive supports. Both suffix arms resolve a relative cell **without touching the repo tree**, which is required because a handoff table names files the plan will CREATE. The verdict is **per row, against ONE task**. **Silent** when the sibling `<plan-folder>.md` is absent, when it carries no `filesTouched` column, or when no candidate resolves. Static and offline. The two codes are **mutually exclusive per row**. A **warning** in v1 only because `RunCommand.cs:198-207` refuses to run a plan with any validation error, and a correct shipped plan can carry a stale cell (plan 28 row 3) — an ERROR would be a retroactive run-blocking gate. **Promotion to ERROR** when a hand-run of this code alone across every plan carrying the convention produces only genuine defects |
| `GR2069` | warning | `HandoffRowSplitAcrossTasks` — every path a handoff row names is writable by *some* task, but **no single task** can write them all: the row is delivered by several tasks and each half must be reachable by the task implementing *that* half. Shared extraction (plan 31 §4, issue #553): candidates are backticked code spans in the plan document's implementation-handoff table carrying a `/` or a file extension; a candidate is **resolvable** only when its first path segment equals a **whole** path segment of some `writeScope` entry in the plan (so a vague fragment like `Cli/Commands/` — where the real segment is `Guardrails.Cli` — is dropped silently rather than reported). A **concrete** candidate is covered by `WriteScope.IsInScope(candidate, [entry])`, by equality, or by a **segment-aligned path suffix** of an entry; a **glob** candidate is covered when `IsInScope(entry, [candidate])` or `IsInScope(entry, ["**/" + candidate])` — **arguments swapped**, the only direction the primitive supports. Both suffix arms resolve a relative cell **without touching the repo tree**, which is required because a handoff table names files the plan will CREATE. The verdict is **per row, against ONE task**. **Silent** when the sibling `<plan-folder>.md` is absent, when it carries no `filesTouched` column, or when no candidate resolves. Static and offline. The two codes are **mutually exclusive per row**. A **confirm**, not a fault: a deliberately split row legitimately triggers it, and the message says so in its own words. It is a **separate code from GR2068 by design** — it fires on 3 of 10 rows of a correct plan, and under one shared code a reviewer learns to skim the code itself, taking GR2068's precision with it (#229). **Should probably never be an ERROR**: it reports a shape the check cannot adjudicate, so blocking on it would refuse a plan whose author already made the right call. Note it is GR2069, not GR2068, that catches both plan-28 failures |

8. **§3.4, beside the `GR2042` paragraph (line ~987).** Two sentences pointing at the new §9.6 row and
   naming the §11 Risk 2 tension, so the next author who meets both at once finds the reconciliation.

9. **`src/Guardrails.Core/Loading/DiagnosticCodes.cs:991-1001`.** The marker reads *"CURRENT next-free
   code: GR2068 … take GR2068 and update this line"*. Take **GR2068 and GR2069**, advance the marker to
   **GR2070**.
   The reserved-by-name gaps **GR2060** (doc 19), **GR2061** (doc 18) and **GR2054** (doc 17 §13.2) are
   untouched, and the `GR10xx` ladder is restated unchanged — the block's own note says a doc stating only
   one ladder is half a fact.

10. **`.claude/skills/guardrails-domain-knowledge/SKILL.md`.** Execution semantics moved (salvage now fires
    on escalation, scope-filtered) and a new validate diagnostic exists. Affected sections only.

11. **`.claude/agents/guardrails-architect.md`.** The **`filesTouched` convention** is now load-bearing:
    every path in a handoff table is a backticked path or glob; prose stays outside the backticks; a
    relative path must be a true **segment suffix** of the real path (`Prompts/Foo.cs` resolves,
    `Cli/Commands/` does not, because the real segment is `Guardrails.Cli`); a row claiming a directory
    must be backed by a task authorized for it. This is where the convention belongs, because the
    architect writes the table.

---

## 13. Implementation handoff

Sequenced; each stage green before the next. Stages 1–3 close **#554**, 4–5 close **#553**, 6–9 close
**#545 part 3**, and 10 closes the SSOT.

> **Why stage 8 is two rows.** An earlier revision handed the watch AND its wiring to one stage, five
> files in one row. That task carried the structural over-scope fingerprint `GR2042` fires on: a
> fan-in sink whose every guardrail miss re-runs the whole five-file change, and — the sharper half of
> #378 — one that concentrates the first real exercise of every integration path in a single action
> that cannot fix the cross-file bug it finds. Since this plan is *itself* run unattended (§9), a
> cheap retry is worth more than a tidy table. Split **by collaborator**: the watch is one deliverable
> verified by unit tests, the wiring is another verified by a real run. The discovery-heavy work — the
> baseline, the ignore list, the `Poll`/`Rebaseline` semantics — leaves with the watch, which is why
> neither half is turn-heavy enough to warrant a `maxTurns` bump. **The two rows also keep this table
> self-covering:** each half owns its paths outright, so neither row trips the GR2069 this plan
> introduces, and the split is recorded here rather than demonstrated as noise.

**Every `writeScope` below is pinned verbatim, as concrete paths.** This is an instruction to
`/plan-breakdown`, not a suggestion: across every plan folder in this repo — **289 `task.json` files, 483 `writeScope`
entries** — **zero** contain a glob, and the breakdown skill's own guidance says *"no vacuous `**`"*. A
row whose `filesTouched` said `tests/…/**` while the breakdown emitted concrete paths would make plan 31's
own table fail GR2068 or GR2069 on its first run. The `filesTouched` column and the `writeScope` array below are
therefore the **same list**, per row.

| # | Agent | filesTouched | `writeScope` (verbatim) | Deliverable |
|---|---|---|---|---|
| 1 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/Execution/EscalationSalvageTests.cs`, `tests/Guardrails.Integration.Tests/EscalationSalvageTests.cs` | the same two paths | #554's pins (§3.5, §8). Core + integration; the integration file carries the real-git-segment pins. **Every assertion is on an observable artifact** — a file, a ref, a composed string — and none names a new API member, which is what lets these compile today and lets stages 2–3 stay test-free. Guardrails: `build-passes`, then `tests-fail-on-stubs` (the compiling tests observed RED). |
| 2 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/TaskExecutor.cs`, `src/Guardrails.Core/Execution/GitWorktreeProvider.cs`, `src/Guardrails.Core/Execution/AttemptJournaler.cs`, `src/Guardrails.Core/Execution/RetryPolicy.cs` | the same four paths | Preserve on the escalation path; `PreserveAttemptToRef`'s `restrictToScope` filter (§3.4 divergence 3); the retention cap; `AppendSalvageSection` / `AppendHeader` `internal` + the defaulted `SalvageFraming`. **The `Retry` framing's bytes must not move** — guarded by the two shipped salvage suites passing untouched. |
| 3 | `guardrails-harness-developer` | `src/Guardrails.Core/Prompts/PromptContext.cs`, `src/Guardrails.Core/Execution/DependencyContextBuilder.cs`, `src/Guardrails.Core/Prompts/PromptComposer.cs`, `src/Guardrails.Core/Execution/Scheduler.cs` | the same four paths | The forward carry (optional `PriorAttemptRef` members, filled by probing `prior-attempt.patch`), the `PriorAttempt` routing block in the composed prompt, and the escalation `Context`. Commit body carries **`Fixes #554`**. |
| 4 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/Loading/HandoffScopeCoverageTests.cs` | the same path | §4.9's nine pins as fixture plan folders built in temp dirs. Assertions use the **literals** `"GR2068"` / `"GR2069"`. **Pins 1 and 2 are the two REAL plan-28 rows in their broken state and both assert `GR2069`** — the acceptance criterion, not invented shapes; pin 2 also asserts the SILENT direction after the fix. Pin 3a pins the codes mutually exclusive. Pins 6 and 7 assert on the **full diagnostic list**. Pin 5a must **fail** under the un-swapped form. |
| 5 | `guardrails-harness-developer` | `src/Guardrails.Core/Loading/HandoffScopeCoverage.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs` | the same three paths | The table locator, extractor and coverage check (new file); a `ValidateHandoffScopeCoverage(plan, diagnostics);` line added to `PlanValidator.Validate` (declared `:51`) beside its two `writeScope` siblings at `:76-77` (`ValidateWriteScopes`, `ValidateStructuralOverScope`); the `GR2068` (`HandoffPathUnreachable`) and `GR2069` (`HandoffRowSplitAcrossTasks`) constants with the marker advanced to **GR2070** — no new path is needed for the second code, `DiagnosticCodes.cs` is already in this row. Carries §4.9 pin 8's grep guardrail. **`Fixes #553`**. |
| 6 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/LivePlanEditWatch.cs` | the same path | **Stub stage** — the §5.2 signature declared and **inert** (members throw `NotImplementedException`), following `docs/plans/model-tiering-stage-3/wave-03-operator-surfaces/tasks/01-stub-the-observer-seam/`. Guardrails, cheapest-first: `build-passes`, then a `stubs-declared-and-inert` check asserting each declaration is present **and** each body is inert, on comment-and-string-literal-stripped source, plus a zero-match guard. |
| 7 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/Execution/LivePlanEditWatchTests.cs`, `tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs` | the same two paths | §5.5's four pins, including the **JIT-breakdown negative pin** and the outcome-inertness pin. Guardrails: `build-passes` (the stub makes them compile), then `tests-fail-on-stubs`. |
| 8 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/LivePlanEditWatch.cs` | the same path | The watch **implemented** over stage 6's inert stubs: the per-**file** definition-surface baseline over `TaskDefinitionFiles.Enumerate`, the ignore list applied HERE and **not** in `HashText` (§5.2), and the `Poll`/`Rebaseline` semantics. Verified by the **Core** unit suite, which drives the watch directly and needs no run — so this half's retry is cheap and its failures are local. Guardrails: `build-passes`, then the filtered Core `tests-pass`. |
| 9 | `guardrails-harness-developer` | `src/Guardrails.Core/Execution/Scheduler.cs`, `src/Guardrails.Core/Execution/DecisionEntry.cs`, `src/Guardrails.Core/Execution/RunReport.cs`, `src/Guardrails.Cli/Commands/RunCommand.cs` | the same four paths | The watch **wired**: its two Scheduler poll sites and the **five** plan-wide re-baseline hooks (§5.3); the `plan-edit` boundary and `observed` tokens; `RunReport.Observations`; the end-of-run rendering. It emits through the shipped `DecisionRecorded`, so **no observer or decorator is touched**. Verified by the **Integration** suite (§5.5's five pins). Depends on stage 8 and — for the `Scheduler.cs` overlap — on stage 3. **`Fixes #545`**. |
| 10 | `guardrails-skill-author` | `docs/plans/02-schemas-and-contracts.md`, `.claude/skills/guardrails-domain-knowledge/SKILL.md`, `.claude/agents/guardrails-architect.md` | the same three paths | §12's edits, items 1–8 and 10–11. Item 9 lands with stage 5 (a code comment). |

> **Overlapping write scopes, and why each is expected.** `Scheduler.cs` is claimed by stages 3 and 9;
> `LivePlanEditWatch.cs` by stages 6 and 8 — the latter is the canonical TDD stub+impl pair
> `WriteScope.OverlappingWriteScopeHint` already documents as EXPECTED. Overlap serializes those tasks,
> which costs nothing because this plan is strictly sequential.

> **Closing keywords are not optional (#547's lesson).** A `fix(#553):` conventional-commit **scope is not
> a closing keyword** — four issues stayed open for a day because of exactly that. Stages 3, 5 and 9 must
> each carry a literal `Fixes #NNN` line in the commit body, and the PR body must repeat it.

> **`.claude/` writes need `stagingOutputs`.** Stage 10 touches `.claude/skills/**` and `.claude/agents/**`;
> in worktree mode a task action cannot write under `.claude/` directly (SSOT §3.5 / §9). The task must
> declare `stagingOutputs`, and its `writeScope` gates the post-move destinations.

---

## 14. Decisions this plan leaves to the maintainer

Three of this section's four earlier entries are now closed and folded into the text: the per-row
coverage form is **adopted** (§4.5, with the union form's epitaph); the definition-hash-at-settle false
green is filed as **#556** (§5.1, §10); `WaveBreakdownInvoker`'s unbounded write authority is filed as
**#557** (§5.3, §10); and the watch does **not** cover `guardrails.json` in v1 (§10). One question is
genuinely open, and it is deliberately not answered here because answering it costs a migration:

1. **Does `HashText.EnumerateFolderFiles` get the ignore list, and who pays for the drift wave?**
   §5.2 puts `.DS_Store` / `Thumbs.db` / `*.swp` / `*.orig` / `*.rej` filtering in `LivePlanEditWatch`
   only, so the watch is strictly quieter than the definition hash and never noisier. But the underlying
   fact stands: those files are currently **part of a task's definition**, so a stray editor artifact in a
   `guardrails/` folder changes `TaskDefinitionHash` and `PlanDefinitionHash`. Fixing it centrally is two
   lines and moves **every recorded definition hash** — turning the next resume of every affected plan
   into a definition-drift halt, and re-staling every `state/guardrails-review.json` keyed on
   `PlanDefinitionHash` (§13 of the SSOT). *No recommendation.* It is a real defect with a real blast
   radius, and it wants its own change with the one-time drift wave planned for rather than a line in a
   hardening plan.
