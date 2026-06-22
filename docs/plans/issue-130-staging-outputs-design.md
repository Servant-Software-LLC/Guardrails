# Architecture: `stagingOutputs` — autonomous `.claude/` deliverables (issue #130)

**Status:** design-of-record. Owner: architect. Implementer: `guardrails-harness-developer`
(reuses this worktree/branch `issues/u1-stagingoutputs`). The lead reviews this before
implementation. **Schema SSOT deltas are spelled out verbatim in the final section** —
they land in `docs/plans/02-schemas-and-contracts.md` **in the same change** that implements
the field (invariant 4).

---

## What's being asked

A plan task whose deliverable lives under `.claude/` (a new skill at
`.claude/skills/<name>/SKILL.md`, a slash command under `.claude/commands/`, an agent persona
under `.claude/agents/`) can **never complete autonomously**: Claude Code's sub-agent runtime
blocks automated writes under `.claude/` **by path pattern**, even when `permissionMode` is
`acceptEdits`. The shipped #104 mitigation (SSOT §9.3) only *detects* the wall and halts
honestly to `needs-human` — it is explicitly a detect-and-halt, not a fix. #130 is the
architect-scoped **full autonomous fix**: a `task.json` **`stagingOutputs`** contract that lets
the action write its deliverable to a staging location the runtime permits, which the harness
then moves into the real `.claude/` path — running with full host permissions, outside the
sub-agent sandbox — *after the action succeeds and before guardrails run*, so the task's
guardrails verify the real `.claude/` artifact and the task goes green unattended.

**Ambiguity named + narrowed.**

1. **`{from,to}` mapping vs glob list.** #104 Option A sketched a `{from,to}` pair; the #130
   title floats a `["**/.claude/...**"]` glob. I narrow to a **`{from, to}` directory/glob
   mapping** (the design below), NOT a bare glob list. Rationale in *Design*: a bare glob is
   ambiguous about the destination (it names what was produced, not where it goes), forces the
   action author to write to the *exact* real-relative shape inside staging, and cannot express
   "I wrote to `build/skill/` → land it at `.claude/skills/foo/`". `{from,to}` makes the
   staging→real mapping explicit and lintable.

2. **Where staging lives.** I narrow to **inside the segment worktree** at a reserved,
   git-ignored, non-`.claude/` prefix (`.guardrails-staging/<task-id>/`), NOT a path under
   `state/` (issue #104's sketch) and NOT outside the worktree. Rationale in *Design → Seams*:
   the runner's cwd is the segment worktree and a dir outside it is unreachable without an
   `--add-dir` grant (the exact reachability wall the AI-merge worker hit, §9.1); a path under
   the plan folder's `state/` is in a *different* tree than the segment worktree the action
   writes and isn't where `Integrate` commits.

3. **Does it subsume #85?** Yes — stated explicitly in *Design → #85 subsumption*. #85
   ("`acceptEdits` doesn't cover `.claude/` in worktrees") cannot be fixed by permission config
   because the block is by path pattern, not by checkout; `stagingOutputs` is the mechanism, and
   #85 should close as *subsumed* (not *fixed by config*).

---

## Placement (harness | skill | schema | docs | v2 | out of scope)

- **Schema:** a new `task.json` field `stagingOutputs` (§3) + a new validation diagnostic
  **GR2024** + a new `GUARDRAILS_*` env var (§5.1) + a `PromptComposer` action-section addition
  (§9). **This is the contract change; it lands in `02-schemas-and-contracts.md` in the same
  change.**
- **Harness:** `RawTask`/`TaskNode` field, `PlanValidator` (GR2024), `PromptComposer`
  (`## Staging outputs` action section), `TaskExecutor.RunAttemptAsync` (the MOVE step + staging
  env var + retry staging-clear), and a small `StagingMover` collaborator. **v1.**
- **Skill (`plan-breakdown`):** out of scope for *this* slice but **named as the natural
  follow-up** — `plan-breakdown` should detect a `.claude/`-targeted deliverable and emit
  `stagingOutputs` + a re-targeted action prompt (today it generates structurally-incapable
  tasks, #104 Option C). Flagged, not implemented here.
- **Not v1 / not in this design:** a general `allowedPaths`/`trustedPaths` permission-override
  field (#104 Option B / #85 Option B) — it asks Claude Code to grant `.claude/` writes, which
  the runtime refuses; staging *sidesteps* the runtime instead of fighting it. A multi-runner
  abstraction for "which paths each runner blocks" — YAGNI; the `.claude/` block is a known
  Claude-Code fact, and `stagingOutputs` is runner-agnostic by construction (it never asks the
  runner to write the blocked path at all).

---

## Invariants in play

- **(2) Harness is the single writer of merged state; children get snapshots, write fragments.**
  *Strained, then respected.* The MOVE is the harness writing **files into the segment worktree**
  — a child-produced deliverable relocated by the harness. This is NOT a state write (state.json
  stays harness-single-writer) and NOT a write to the user's checkout (the move is inside the
  *segment worktree*, which the harness already owns and commits via `Integrate`). It is the same
  class of harness-owned worktree write as `git add -A` already is. **It must be added to §5.3's
  enumerated "harness writes to the workspace" cases with its own containment analysis** (§5.3
  mandates exactly this for any new harness write) — see SSOT deltas.

- **(5) Honest halts — nothing marked done unverified; needs-human is a feature.** *Respected and
  strengthened.* The MOVE happens **before guardrails**, so guardrails verify the **real
  `.claude/` artifact** — the task only goes green if the deliverable is genuinely in place and
  passes. A failed move, an empty staging source, or a guardrail failure on the moved artifact
  all halt honestly (retry, then `needs-human`). `stagingOutputs` never marks a `.claude/`
  deliverable done without the real-path artifact existing and passing.

- **(3) Verdicts from files, never exit codes** — untouched; guardrails still read verdict files.
  Noted only to confirm the MOVE introduces no exit-code-as-verdict coupling.

- **(6) Plain files, light setup.** *Respected.* Staging is a directory and a `move`; no daemon,
  no DB, no new dependency.

- **Worktree isolation (the plan-08 concurrency boundary).** *Central.* The move is scoped to the
  task's own segment worktree; staging is per-task (`.guardrails-staging/<task-id>/`), so two
  parallel tasks never collide. The user's checkout is still read-only.

---

## Design

### The shape (`{from, to}` mapping)

```jsonc
// tasks/<id>/task.json
{
  "description": "Author the certify-knowledge skill",
  "dependsOn": ["04-build-kb-index"],
  "stagingOutputs": [
    { "from": "skill/**", "to": ".claude/skills/certify-knowledge/" }
  ],
  "writeScope": [".guardrails-staging/05-certify-knowledge/**"],
  "action": { "path": "action.prompt.md", "runner": "claude" }
}
```

- **`from`** — a path or glob **relative to the per-task staging root**
  `GUARDRAILS_STAGING_DIR` (= `<segment-worktree>/.guardrails-staging/<task-id>/`). The action
  writes its deliverable *there*. A glob (`skill/**`) moves a subtree; a bare file
  (`SKILL.md`) moves one file.
- **`to`** — a **workspace-relative** destination under `.claude/`. A trailing `/` (or a `to`
  that names an existing/implied directory) means "land the matched `from` subtree under this
  directory, preserving the relative structure below the glob's fixed prefix"; a `to` naming a
  file moves one file to that exact path.
- **Semantics of the move:** for each entry, the harness resolves `from` against
  `GUARDRAILS_STAGING_DIR`, computes each matched source's path *relative to the glob's fixed
  prefix*, and lands it under `to` in the segment worktree. Overwrite-in-place (the real
  `.claude/` path may already exist from a prior task and be intentionally replaced — last task
  in the chain wins, the same as any file write). Directories are created as needed.

**Why `{from,to}` over a bare glob list.** A bare `[".claude/skills/foo/**"]` glob describes the
*real* path but says nothing about where in staging the action wrote it — it forces the action to
mirror the exact `.claude/`-relative shape *inside* staging, and it can't express a rename or a
flatten. `{from,to}` makes the contract a literal map: "what you wrote (under staging) → where it
lands (under `.claude/`)". It is also what makes GR2024's "`to` must be under `.claude/`" check
crisp.

### Validation — GR2024

`stagingOutputs` is **optional**; absent ⇒ no staging (today's behavior, unchanged). When
present, `PlanValidator` enforces (all **errors** — a malformed staging contract would silently
fail to deliver):

- **GR2024** fires when **any** of:
  - the array is present but **empty** (`"stagingOutputs": []`) — declares staging but stages
    nothing; almost certainly a mistake.
  - an entry has a missing/empty `from` **or** a missing/empty `to`.
  - a `to` does **not** resolve under `.claude/` (workspace-relative, after normalization —
    `.claude/`, `./.claude/`, nested are fine; anything whose first normalized segment is not
    `.claude` is rejected). **This is the load-bearing check:** `stagingOutputs` exists *only*
    to land `.claude/` deliverables; a non-`.claude/` `to` is either a misunderstanding (use a
    normal action write) or an escape attempt.
  - a `to` escapes the workspace (absolute, or `..` segments that climb out) — same family as
    GR2019 for `writeScope`, reused phrasing.
  - a `from` escapes the staging root (absolute, or `..` climbing above
    `GUARDRAILS_STAGING_DIR`) — the action may only stage *within* its staging dir.

  One code, GR2024, with a specific per-cause message (mirrors how GR2019/GR2020 carry one code
  with a precise reason string). *Not* a warning: a malformed `stagingOutputs` would produce a
  task that runs, writes to staging, moves nothing (or the wrong thing), and then fails its
  `.claude/` guardrail for a reason that was knowable at validate time — exactly the GR2022/GR2024
  philosophy of turning a knowable runtime cascade into a load-time catch.

### Env contract — `GUARDRAILS_STAGING_DIR`

A new §5.1 variable, **set for actions only**, **only when the task declares `stagingOutputs`**:

| Variable | Set for | Meaning |
|---|---|---|
| `GUARDRAILS_STAGING_DIR` | actions, when `stagingOutputs` declared | Absolute path to the task's staging root `<workspace>/.guardrails-staging/<task-id>/`. The action writes its `.claude/`-destined deliverable HERE (the runtime permits it), under the relative `from` paths the task declares; the harness moves staged outputs into their real `.claude/` paths after the action succeeds and before guardrails run. |

- The harness **pre-creates** the staging dir before the action runs (unlike `STATE_OUT`, which
  is not pre-created) — the action should be able to `Write` into it without first creating the
  tree, and a pre-created empty dir is the signal "stage here".
- It is **absent for guardrails** (by the time guardrails run, the move has happened and the real
  `.claude/` artifact is the thing to verify — a guardrail reading `GUARDRAILS_STAGING_DIR` would
  be inspecting pre-move scaffolding, an anti-pattern). Absent for `--revalidate-task` (no action
  ran; nothing was staged).

**PromptComposer addition (the action-contract instruction).** A new action section, emitted
**only when the task declares `stagingOutputs`**, after `## Output contract`:

```
## Staging outputs

This task's deliverable is destined for a path under `.claude/`, which the runtime
blocks you from writing directly. Write it instead to this absolute staging directory:

`<GUARDRAILS_STAGING_DIR>`

Place files under these relative paths; after you finish, the harness moves them into
their real `.claude/` locations (it has the permissions you don't), then runs the
guardrails against the REAL `.claude/` paths:

- `<from>`  →  `<to>`
  (e.g. write `skill/SKILL.md` under the staging dir; it lands at
  `.claude/skills/certify-knowledge/SKILL.md`)

Do NOT attempt to write under `.claude/` directly — it will be refused. Stage, and the
harness delivers.
```

Agents read instructions, not env vars (§5.1) — so the staging dir **and** the `from→to` map are
embedded verbatim in the prompt body, exactly as the AI-merge worker embeds its four paths.

### Where staging lives + the staging→real mapping

```
<segment-worktree>/
├── .guardrails-staging/            # reserved, git-ignored, harness-managed (NOT committed)
│   └── 05-certify-knowledge/       # per-task root = GUARDRAILS_STAGING_DIR
│       └── skill/
│           └── SKILL.md            # the action wrote this (runtime permitted)
└── .claude/
    └── skills/certify-knowledge/
        └── SKILL.md                # the harness MOVED it here, before guardrails
```

- **Staging root:** `<workspace>/.guardrails-staging/<task-id>/`. The `<workspace>` is the
  effective cwd — the **segment worktree** in worktree mode, the plan `workspace` in serial mode.
  So staging is always *inside the tree the runner can write and `Integrate` commits*.
- **Reachability:** the runner's cwd is the worktree; `.guardrails-staging/...` is a normal,
  permitted subtree (not `.claude/`) — no `--add-dir` grant needed (the §9.1 reachability wall is
  avoided by construction).
- **Git hygiene:** the harness **deletes the entire `.guardrails-staging/` tree after the move
  and before `Integrate`'s `git add -A`**, so staging scaffolding never reaches a commit. As a
  belt-and-braces second line, the harness also ensures `.guardrails-staging/` is git-ignored in
  the segment worktree (a generated `.git/info/exclude` entry — *not* a tracked `.gitignore`, so
  the user's repo is never modified). Deletion is the primary guarantee; the exclude only protects
  against a move that partially failed.

### MOVE timing (the load-bearing ordering decision)

The move slots into `TaskExecutor.RunAttemptAsync` at a **single, precise** point. Current order
(verified in `TaskExecutor.cs`): action → needsHuman short-circuit → permission-wall check →
transient/failure routing → **write-scope check** → **guardrails** → fragment-validate-for-settle
→ (Scheduler) B1 settle (`MergeFragmentIntoState` → `Integrate` does `git add -A` + commit).

**The move goes: after the action succeeds, BEFORE the write-scope check, BEFORE guardrails.**

```
action succeeds
  → [NEW] StagingMove: for each stagingOutputs entry, move GUARDRAILS_STAGING_DIR/<from>
          into <segment-worktree>/<to>; then delete .guardrails-staging/ tree
  → write-scope check        (now sees the REAL .claude/ paths as the changed surface)
  → guardrails               (verify the REAL .claude/ artifact)
  → fragment-validate-for-settle
  → (Scheduler B1) MergeFragmentIntoState → Integrate (git add -A commits the .claude/ files)
```

**Why before the write-scope check (not after).** The write-scope check (§3.4) stages the
worktree and diffs the index against `taskBase` — it asserts every *changed* path is in scope. If
the move ran *after* the check, the check would see only the staging writes (or nothing, since
staging is deleted) and the real `.claude/` files would land unchecked. Running the move *before*
the check means the check sees the **real `.claude/` paths** as the changed surface — which is
correct: the task's `writeScope` should authorize the `.claude/` destinations (and the staging
root). This also means **`writeScope` and `stagingOutputs` compose cleanly**: a `.claude/`-staging
task declares `writeScope: [".claude/skills/certify-knowledge/**", ".guardrails-staging/**"]`
(the staging prefix is moved-away-and-deleted so it nets to zero changed paths, but listing it is
harmless and self-documenting; `plan-breakdown` can emit just the `.claude/` destination since the
staging tree is deleted before the diff).

**Why before guardrails (not after).** So guardrails verify the **real** artifact — the whole
point. A guardrail like `01-skill-exists` (`test -f .claude/skills/certify-knowledge/SKILL.md`)
only passes against the moved file. This is the honest-halt guarantee (invariant 5).

**Ordering vs fragment-merge.** Strictly **before**. The fragment merge (state.json) is the last
green-path step (Scheduler B1 step 1), gated on guardrails passing; the move is gated *earlier*
(action success) so guardrails — which run before any settle — see the real artifact. The
sequence is move → write-scope → guardrails → (settle: fragment-merge → git commit). No
interleaving.

**Ordering vs (worktree-mode) Integrate / segment commit.** Strictly **before**
`Integrate`'s `git add -A`. The moved `.claude/` files must be present in the segment working
tree when `Integrate` stages-and-commits — which they are, because the move lands them in the
segment worktree two steps earlier. The deleted `.guardrails-staging/` tree is gone by commit
time, so the commit carries the `.claude/` deliverable and **not** the staging scaffolding. The
plan branch (and, on `--merge-on-success`, the user's branch) thus receives a clean `.claude/`
artifact with no `.guardrails-staging/` residue.

**Ordering vs the segment integration/settle in worktree mode — single-writer note.** The move is
done by the **executor inside the per-task segment worktree** (not by the Scheduler under the
integration lock). It is safe outside the lock because it touches only this task's own segment
worktree, which no other worker shares (worktree isolation). The integration lock continues to
guard only the cross-task plan-branch settle.

### Worktree-mode interaction

- **Is the segment worktree's `.claude/` also blocked?** **Yes** — the Claude Code block is by
  **path pattern** (`.claude/**`), independent of which git checkout the path is in. This is the
  #85 reproduction (the block fired *inside a worktree*). So the action cannot write the segment
  worktree's `.claude/` directly either — staging is required in worktree mode, exactly as in
  serial mode.
- **Does the move happen in the segment or the integration worktree?** **The segment worktree** —
  always. The action writes staging in the segment worktree (its cwd); the harness moves
  staging→`.claude/` in that *same* segment worktree, before guardrails; `Integrate` then commits
  the segment (carrying the `.claude/` files) into the plan branch. The **integration worktree is
  never touched by staging** — it only ever receives already-moved, already-committed `.claude/`
  files via the normal merge. This keeps the move entirely within worktree isolation and entirely
  outside the integration lock.
- **Serial mode (`maxParallelism: 1`, shared workspace):** staging root is
  `<plan-workspace>/.guardrails-staging/<task-id>/`; the move lands files in the user's checkout
  `.claude/`. This is the one case where the move writes the user's checkout — but **only** the
  `.claude/` deliverable the task is *for*, and serial-shared-workspace mode already runs all
  child writes against the user's checkout (it is the documented serial trade-off; §7.1
  revalidate and a `maxParallelism:1` run already write there). No new exposure beyond what serial
  mode already grants. Worktree mode (the default, `maxParallelism:3`) keeps the user's checkout
  read-only throughout.

### Rollback

- **Action failure** (non-zero / `is_error`): guardrails are skipped, **the move never runs** (it
  is gated on action success). The retry's `git reset --hard taskBase + git clean -fd` cleans the
  segment, **and the harness additionally deletes `.guardrails-staging/<task-id>/`** before the
  next attempt (git clean removes untracked files, so it would catch staging too — but staging is
  deleted explicitly so the contract does not depend on staging being untracked). On the next
  attempt the staging dir is re-created empty.
- **Move failure** (e.g. an empty staging source for a declared `from` — the action didn't
  produce what it promised, or an IO error): the attempt **fails** with actionable feedback
  ("`stagingOutputs` entry `<from>` matched no files under the staging dir — write your deliverable
  to `<GUARDRAILS_STAGING_DIR>/<from>` before finishing"), the segment is reset, staging cleared,
  and it retries. An empty-source move is treated like a guardrail-class failure (it is a
  deliverable-not-produced condition), not a crash. Repeated failure → `needs-human` via the
  normal exhaustion path.
- **Guardrail failure on the moved artifact:** the standard failed-attempt path — `git reset
  --hard taskBase + clean -fd` removes the moved `.claude/` files from the segment (they were
  never committed), staging is cleared, retry with guardrail feedback. The real-path artifact is
  correctly un-done because it lived only in the segment working tree, never in a commit, until
  the green settle.
- **Crash between move and commit:** the segment worktree is non-green, so resume re-runs the task
  from `taskBase` (the move's files are uncommitted WIP, wiped by the retry reset) — no half-moved
  state survives into a commit. The move is *not* journaled as a distinct step; its only durable
  effect is the committed `.claude/` files, which only exist on a green settle.

### #85 subsumption — explicit

**`stagingOutputs` SUBSUMES #85.** #85 asks that `acceptEdits` cover `.claude/` writes *inside
worktrees*. It **cannot be granted by permission config** — the Claude Code runtime blocks
`.claude/**` by path pattern regardless of `permissionMode` or which checkout the worktree is, so
there is no `allowedTools`/`permissionMode`/`trustedPaths` value that unblocks it (that is #85
Option B and #104 Option B, both dead ends against the runtime). `stagingOutputs` is the
mechanism that makes a `.claude/`-writing task succeed *unattended in a worktree*: the action
writes staging (permitted), the harness moves it (full host permissions, outside the sandbox).
**Recommendation: close #85 as subsumed by #130** — not "fixed by making acceptEdits broader,"
but "made unnecessary; the supported path for a `.claude/` deliverable in a worktree is
`stagingOutputs`." The §9.3 detect-and-halt remains the safety net for a `.claude/`-writing task
that *didn't* declare `stagingOutputs` (it still halts honestly with feedback that now points at
`stagingOutputs` as the fix).

---

## Devil's-advocate self-critique

**Strongest counter-argument: "The move-before-write-scope-check ordering smuggles harness-written
files past the deterministic write-scope gate — the harness writes `.claude/` files the *action*
never wrote, then the check `approves` them as if the action produced them. That breaks the
write-scope check's meaning (\"every path the *action* changed is in scope\")."**

This is the sharpest objection and it is partly right: after the move, the changed surface the
write-scope check sees includes harness-moved files, not purely action-written ones. My response:

1. **The provenance is still the action's** — the harness moved *exactly the bytes the action
   wrote to staging*, to the destination the *task author declared* in `stagingOutputs`. The move
   is a deterministic, declared relocation, not harness-invented content. So "the action is
   responsible for these `.claude/` paths" remains true; the harness is a courier, not an author.
2. **The write-scope check still does its job** — it asserts the *destination* `.claude/` paths
   are in the task's declared `writeScope`. A task that stages a file to a `.claude/` path *not*
   in its `writeScope` still fails the check. The check is not weakened; it is applied to the
   real, post-move surface, which is the surface that actually gets committed.
3. **The alternative is worse.** If the move ran *after* the check, the `.claude/` files would be
   committed entirely unchecked by `writeScope` — *that* would be the real hole. Move-before-check
   is the option that keeps the deterministic gate covering the committed surface.

A residual honesty cost remains: the write-scope check can no longer be read as "the action's
*direct* writes," only as "the committed surface, including declared staging relocations." I judge
that acceptable and will state it in the §3.4 SSOT note (one sentence), because the surface the
check protects — what reaches the commit — is unchanged and still fully gated.

**Second counter-argument: "Why a new env var + composer section at all? Just let the action write
`.claude/` and have the harness *retro-move* anything blocked."** Rejected: the harness can't
divine *where* a blocked write was *meant* to go without the action telling it, and a retro-move
would depend on parsing refusal messages (fragile, the §9.3 quarantine's job) rather than a
declared contract. The explicit `stagingOutputs` map is deterministic and lintable (GR2024); a
retro-move is a guess.

**Third: "Scope creep — is `{from,to}` glob semantics over-engineered vs a flat file list?"**
Considered. A flat `["SKILL.md"]`-style list under an implied single `to` is simpler but can't
express a multi-file skill with subdirs (`references/*.md`) or two destinations from one task. The
glob `{from,to}` is the minimum that covers the real `.claude/` deliverable shapes (a skill folder
is a tree). I am *not* adding rename/transform/templating — that would be over-engineering. The
glob is bounded to "move this subtree there."

---

## Implementation handoff

**Agent:** `guardrails-harness-developer` (this design forbids the architect writing `src/`).
Build+test is a hard gate; the SSOT delta lands in the same change.

**`filesTouched` contract (sequenced):**

1. **SSOT first** — `docs/plans/02-schemas-and-contracts.md`: apply the four deltas below (§3
   field, §5.1 row + composer note, §3.4 one-sentence note, §5.3 new harness-write case, §9.3
   residual update, GR2024 in the diagnostics list). *Contract before code (invariant 4).*
2. **Model + loader** — `src/Guardrails.Core/Loading/RawManifests.cs` (`RawTask.StagingOutputs`
   as `List<RawStagingOutput>{ From, To }`), `src/Guardrails.Core/Model/TaskNode.cs`
   (`IReadOnlyList<StagingOutput>? StagingOutputs`), a `StagingOutput` record, and
   `src/Guardrails.Core/Loading/PlanLoader.cs` to bind it.
3. **Diagnostic** — `src/Guardrails.Core/Loading/DiagnosticCodes.cs`: add
   `StagingOutputsInvalid = "GR2024"` with the XML-doc rationale; `PlanValidator` emits it for
   the five GR2024 causes.
4. **Composer** — `src/Guardrails.Core/Prompts/PromptComposer.cs`: `AppendStagingOutputs(...)`
   emitted from `ComposeAction` only when the task declares `stagingOutputs`; thread the
   `from→to` map + `GUARDRAILS_STAGING_DIR` value through `ActionRunner`/`DependencyContextBuilder`
   to the composer (the same plumbing `stateOutPath` uses).
5. **Mover + executor** — a `src/Guardrails.Core/Execution/StagingMover.cs` (pure: takes staging
   root, worktree root, the `from→to` list; returns moved/empty-source result) and the wiring in
   `src/Guardrails.Core/Execution/TaskExecutor.cs` `RunAttemptAsync`: pre-create
   `GUARDRAILS_STAGING_DIR` + add it to `BuildEnvironment` (action env only, gated on
   `stagingOutputs`); run the move **after action success, before the write-scope check**; on
   empty-source/IO failure produce a failed attempt with `RetryPolicy.ForStagingFailure(...)`;
   delete `.guardrails-staging/` after a successful move and before returning; clear staging on
   the F2 retry reset.
6. **Tests** — `guardrails-test-author` (or the harness-developer's own unit tests): GR2024
   table (empty array, missing from/to, non-`.claude/` `to`, workspace escape, staging escape);
   a move-lands-and-guardrail-sees-real-path integration test; a guardrail-fails-then-retry-resets
   test proving the moved file is removed on reset; a serial-mode and a worktree-mode move test;
   an empty-staging-source → retry-feedback test.

**Sequencing:** 1 → 2 → 3 (validate compiles/tests green) → 4 → 5 → 6. The move step (5) is the
only behaviorally-subtle piece; land it last with its tests.

---

## Proposed plan-document edits (`docs/plans/02-schemas-and-contracts.md`)

> The architect proposes; the user approves; then the architect applies. These are the **exact**
> edits.

### Edit 1 — §3 `task.json`: add the `stagingOutputs` field

In the §3 `tasks/<id>/task.json` JSONC block, **after** the `writeScope` field block and **before**
`"retries": 3,`, insert:

```jsonc
  "stagingOutputs": [                                // optional; autonomous .claude/ delivery (§3.5). Absent ⇒ none.
    { "from": "skill/**", "to": ".claude/skills/foo/" }  // action writes <from> under GUARDRAILS_STAGING_DIR;
  ],                                                 //   harness MOVES it to <to> after action, before guardrails
```

Then add a new subsection **§3.5** immediately after §3.4:

```markdown
### 3.5 Staging outputs (`stagingOutputs`) — autonomous `.claude/` delivery

A task whose deliverable lives under `.claude/` cannot write it directly: the Claude Code
sub-agent runtime blocks automated writes under `.claude/` **by path pattern**, even under
`permissionMode: acceptEdits`, in the user's checkout AND in a segment worktree (issues
#104/#85, §9.3). `stagingOutputs` is the **autonomous fix**: the action writes its deliverable to
a harness-managed staging dir the runtime permits, and the harness — running with full host
permissions, outside the sub-agent sandbox — **moves** the staged outputs into their real
`.claude/` paths **after the action succeeds and before the task's guardrails run**, so the
guardrails verify the real `.claude/` artifact and the task goes green unattended.

`stagingOutputs` is an optional list of `{ "from", "to" }` mappings:

- **`from`** — a path or glob relative to `GUARDRAILS_STAGING_DIR` (§5.1), the per-task staging
  root `<workspace>/.guardrails-staging/<task-id>/` (the segment worktree in worktree mode, the
  plan workspace in serial mode). The action writes its deliverable under this relative path.
- **`to`** — a workspace-relative destination **under `.claude/`**. A trailing `/` lands the
  matched `from` subtree under that directory preserving relative structure; a file `to` moves one
  file.

**The move** runs in the task's segment worktree, after action success, **before the write-scope
check** (§3.4) and the guardrails — so the changed surface the write-scope check and the guardrails
see is the real `.claude/` path. The harness deletes the entire `.guardrails-staging/` tree after
the move and before integration, so staging scaffolding never reaches a commit (a generated
`.git/info/exclude` entry is a belt-and-braces second line; the user's tracked `.gitignore` is
never modified). The move is done by the executor inside the per-task segment worktree (worktree
isolation), not under the integration lock.

**Rollback.** The move is gated on action success, so an action failure never moves. A move that
matches no files for a declared `from`, an IO failure, or a guardrail failure on the moved artifact
all fail the attempt; the retry's `git reset --hard <taskBase> + git clean -fd` removes the
uncommitted moved files and the harness clears the staging dir, so the next attempt starts clean.
Repeated failure settles `needs-human` via the normal exhaustion path. The committed `.claude/`
artifact exists only on a green settle.

**Validation (`GR2024`, error).** A present `stagingOutputs` is rejected when: the array is empty;
an entry has a missing/empty `from` or `to`; a `to` does not normalize to a path under `.claude/`;
a `to` escapes the workspace (absolute or `..` climbing out, as GR2019); or a `from` escapes the
staging root. A malformed staging contract would produce a task that runs, moves nothing, and fails
its `.claude/` guardrail for a load-time-knowable reason — so it is an error, not a warning.

**Composes with `writeScope`.** A staging task's `writeScope` authorizes the real `.claude/`
destination(s) (the surface the write-scope check sees after the move); the staging prefix nets to
zero changed paths because it is deleted before the diff. **Subsumes #85:** the `.claude/` block is
by path pattern, so no permission-config value unblocks a worktree `.claude/` write;
`stagingOutputs` is the supported autonomous path.
```

### Edit 2 — §3.4: one-sentence honesty note

At the end of the §3.4 paragraph (after the matcher/proof-harness sentence), append:

```markdown
When a task declares `stagingOutputs` (§3.5), the write-scope check runs on the **post-move**
surface: it gates the real `.claude/` destination paths (which the task's `writeScope` must
authorize), not the pre-move staging writes — the surface the check protects (what reaches the
commit) is unchanged and still fully gated.
```

### Edit 3 — §5.1: add the env-var row + composer note

In the §5.1 env-var table, add a row (after `GUARDRAILS_STATE_OUT`):

```markdown
| `GUARDRAILS_STAGING_DIR` | actions, when `stagingOutputs` declared | Pre-created absolute staging root `<workspace>/.guardrails-staging/<task-id>/`. The action writes its `.claude/`-destined deliverable here under the relative `from` paths; the harness moves staged outputs into their real `.claude/` paths after the action succeeds and before guardrails run (§3.5). Absent for guardrails (verify the real path) and for `--revalidate-task` (no action ran) |
```

And in §9's composed-prompt bullet list (the "The composed prompt = body + appended harness
sections" enumeration), add to the actions sections:

```markdown
**staging-outputs contract** (actions, when `stagingOutputs` declared, §3.5): the absolute
`GUARDRAILS_STAGING_DIR` and the `from→to` map embedded verbatim ("write here; the harness moves
it to `.claude/`; do not write `.claude/` directly"), since agents read instructions, not env vars.
```

### Edit 4 — §5.3: add the new harness-write case

§5.3's closing paragraph mandates a containment analysis for any new harness write. Add, before
that closing paragraph:

```markdown
**(C) Staging move (§3.5).** When a task declares `stagingOutputs`, the harness moves the
action's staged files into their real `.claude/` paths **inside that task's own segment worktree**
— after the action succeeds, before the write-scope check and guardrails. *Containment:* the write
is confined to the per-task segment worktree the harness already owns and commits via `Integrate`
(the same tree `git add -A` stages); it is scoped to the task's declared `.claude/` destinations
(gated by the write-scope check on the post-move surface); it runs under worktree isolation, not
the integration lock (no cross-task surface); and the `.guardrails-staging/` source tree is deleted
before integration so no scaffolding is committed. In serial shared-workspace mode the move lands
in the user's checkout `.claude/` — the one documented serial trade-off, no broader than the
existing serial-mode child writes (§7.1). It never writes the integration worktree or the user's
branch outside the existing `--merge-on-success` delivery.
```

### Edit 5 — §9.3: update the residual to point at the now-shipped fix

Replace the §9.3 final "**Residual (honest scope).**" sentence beginning "A full autonomous fix…"
with:

```markdown
The full autonomous fix is the `task.json` `stagingOutputs` contract (§3.5, issue #130): a task
declares the `.claude/` deliverable it produces and a staging path the action writes instead, and
the harness moves the staged output into its real `.claude/` path after the action succeeds and
before guardrails run — so the task completes unattended. The §9.3 detect-and-halt remains the
safety net for a `.claude/`-writing task that did **not** declare `stagingOutputs`; its
`feedback.md` now points at `stagingOutputs` as the fix. The breakdown-time emission of
`stagingOutputs` for `.claude/`-targeted tasks (Option C) is a `plan-breakdown` skill change,
tracked separately.
```

### Edit 6 — diagnostics list

Wherever the GR20xx codes are enumerated (the `DiagnosticCodes` doc table / list), add:

```markdown
- **GR2024** (error) — a `stagingOutputs` entry is malformed: empty array, missing/empty
  `from` or `to`, a `to` not under `.claude/`, a `to` that escapes the workspace, or a `from`
  that escapes the staging root (§3.5).
```
