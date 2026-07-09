# 14 — A `.guardrails/` folder convention? (scoping decision — issue #275)

> **Status: RECOMMENDATION — recommend-only.** Do **not** change the default plan-folder
> location. Formalize `.guardrails/` as a *documented, optional* home; add no new command and
> no v1 behavior change. Rationale below. This is a small scoping decision whose answer is
> effectively "no, not as a default."

## What's being asked

Issue #275 proposes adopting a dot-prefixed `.guardrails/` folder as the **default home** for
plan folders (`<plan>/` task trees) and runtime state — distinct from `.claude/` (Claude Code's
config dir) and `docs/` (documentation). It spun out of #266's design pass. The question is not
"is `.guardrails/` a reasonable name" (it is) — it is **"is a new default-location convention
worth adopting, on top of the fixes already shipped for #266 and #258?"**

Three candidate motivations, assessed in turn:

1. **Write-escape** — give the harness/agent somewhere to write that isn't `.claude/`-restricted.
2. **Hygiene** — the committed-vs-transient split (#258).
3. **Home / namespace** — one recognizable footprint for the tool in a repo (like `.github/`).

The maintainer's framing puts (1) as the original driver and asks whether it is still live.
A coordinator note adds (3) and flags it as likely the strongest surviving case.

**Ambiguity named + narrowed.** "Adopt `.guardrails/`" conflates two very different changes: (a)
**relocating persistent plan folders** (a breaking default change with a migration story) and (b)
**consolidating transient runtime state** (already gitignored / already out-of-tree). This design
separates them — they have opposite cost/benefit — and answers each independently rather than as
one bundled "yes/no."

## Placement

**Docs (recommend-only).** No harness change, no schema change, no new default, no `guardrails
init` command in v1. Not a named v2 bet — recorded instead as a *revisit-if-adoption-friction*
note (see §Recommendation). The one concrete artifact is this doc plus (on approval) a short
"optional home" paragraph in the `plan-breakdown` skill and SSOT §1 that formalizes an *already
partly-documented* option.

## Invariants in play

- **#6 Plain files, light setup — no databases/daemons/SaaS in v1.** A `guardrails init` command
  (Option C) is net-new setup ceremony; a documented convention is not. Favors recommend-only.
- **#4 SSOT is the schema SSOT; a contract change lands there in the same change.** Plan-folder
  *location* is a soft convention, not a schema field — but if we ever change the default, SSOT §1
  and the `plan-breakdown` skill are the two touch points, and they must move together. This
  design keeps them untouched (no default change) and proposes only additive "optional home" prose.
- **#5 Honest halts / #2 harness is the single writer.** Relevant to the write-escape verdict: the
  harness process is *not* subject to Claude Code's permission classifier (only the `claude -p`
  sub-agent is), and `needsHarnessWrite` (#191) already exists precisely because the harness can
  write `.claude/` files the agent cannot. Load-bearing for §1 below.

## Design

### 1. The write-escape question — definitively SUBSUMED by #266 (dead)

**Answer: the write-escape motivation is fully closed. A `.guardrails/` home is NOT necessary on
write-access grounds. No remaining write path — from the harness process or the agent — hits a
`.claude/` restriction because of the plan folder's location.**

Trace of every write during a run, split by *who* writes (the distinction is the whole answer):

**The HARNESS process (a normal .NET process — NOT subject to Claude Code's permission
classifier; can write anywhere, including `.claude/`):**

| Write | Path | Blocked by `.claude/`? |
|---|---|---|
| Merged state | `state/state.json` | No — harness process write |
| Journal | `state/run.json` | No |
| Per-attempt logs | `logs/<runId>/<task>/attempt-N/…` | No |
| Merge conflicts | `state/merge-conflicts.log` | No |
| `.gitignore` scaffold | plan-root `.gitignore` (#258, `PlanGitignore`) | No |
| Segment worktrees | `<worktreeRoot>/<runId>/…` — default system TEMP, **outside the workspace entirely** | No |
| Promoted fragment/verdict | the documented `logs/…/attempt-N/` targets, moved into place by the harness | No |

The harness writing under a `.claude/`-nested plan folder was never the problem — a plain OS
process is not classified. This is doubly confirmed by `needsHarnessWrite` (#191, SSOT §9), whose
entire reason to exist is that the harness process *can* write `.claude/` paths the agent can't.

**The AGENT (`claude -p` — IS subject to the classifier; the only writer that a `.claude/` block
can catch):**

| Agent write | Path | Status |
|---|---|---|
| `GUARDRAILS_STATE_OUT` fragment | staged to `<workspaceRoot>/.guardrails-agent-io/<task>/attempt-N/…`, promoted by the harness (SSOT §9.5) | **Fixed by #266 / PR #276** — never `.claude/`-nested |
| `GUARDRAILS_VERDICT_OUT` verdict | same staging path | **Fixed by #266 / PR #276** |
| The task's own deliverable | governed by `writeScope` + the worktree-containment hook; a `.claude/`-destined deliverable uses the opt-in `stagingOutputs`/#130 mechanism (SSOT §3.5/§9.3) | Orthogonal to plan-folder location; unchanged by any folder convention |

Before #266, a `.claude/`-nested plan put the harness's own always-present `STATE_OUT`/`VERDICT_OUT`
targets (under `logs/…`) inside the block, and *no* per-task `stagingOutputs` opt-in could help
because those targets are harness-internal and exist on every prompt attempt. PR #276 generalized
the staging pattern to those targets **unconditionally for every prompt action/guardrail**: the
sub-agent is handed a `.guardrails-agent-io/` path *inside the effective workspace root* (never
`.claude/`-nested, always inside the containment hook's allowed root), and the harness promotes the
file the instant the sub-agent exits. Verified end-to-end in PR #276 against the live Claude Code
CLI on a plan physically rooted at `.claude/plans/probe/`.

**Conclusion:** #275's own body is correct — #266 "removes this as a bug, structurally, regardless
of plan folder location." A `.claude/`-nested plan is now fully runnable, prompt tasks included.
The write-escape argument for `.guardrails/` is spent. Say it plainly: **do not adopt `.guardrails/`
to unblock writes — there is nothing left to unblock.**

### 2. The hygiene question (#258) — SHIPPED; residual value marginal

`bfac7e8` (PR #284) scaffolds a plan-root `.gitignore` at run-init (`PlanGitignore.EnsureScaffolded`,
called from `StateManager.Initialize`) covering exactly the `RunReset.Fresh` transient set
(`/logs/`, `/state/run.json`, `/state/state.json`, `/state/merge-conflicts.log`,
`/state/rewind-intent.json`, `/state/captured/`). It is a denylist, so committed artifacts stay
tracked by default; it is non-clobbering and idempotent. The committed-vs-transient split #258
described is solved *at the filesystem level, in place*. A new folder adds nothing here — the
foot-gun is already closed for plan folders in *any* location.

### 3. The home/namespace question (Motivation 3) — real but weak, and mostly dissolves on inspection

This is the only surviving *positive* case, and it deserves an honest hearing on its own merits:
a single `.guardrails/` home would give the tool one recognizable footprint (like `.github/`,
`.vscode/`, `.idea/`), and there is already a **toehold** — `guardrails-patterns.md` is documented
(`plan-breakdown` Step 0) to live "at the workspace root **or under `.guardrails/`**," and the
transient dot-folders `.guardrails-staging/` (§3.5) and `.guardrails-agent-io/` (§9.5) already
exist. So the name is effectively reserved-in-practice; formalizing it costs a paragraph.

But when you ask *what would actually move into the home*, the case thins out:

- **Transient runtime state** — already gitignored (#258), and the largest piece (segment
  worktrees) already lives **outside the workspace** under `worktreeRoot` (system TEMP by default).
  There is no scattered persistent transient footprint to consolidate.
- **Config (`guardrails-patterns.md`)** — already `.guardrails/`-optional. Nothing to do.
- **Plan folders** — the *only* persistent thing a home would consolidate. And these are
  **deliberately placed beside their source `.md`** (`plan-breakdown` writes `<plan>/` next to
  `<plan>.md`; `guardrails.baseline` ties the folder to its source). That source↔artifact locality
  is load-bearing: it is the strongest coupling in the system (breakdown regenerates the folder
  from the `.md`) and the "beside the plan" convention makes it obvious with zero indirection.

So Motivation 3, on close inspection, asks us to **break the beside-the-`.md` locality (a genuine
discoverability property) to gain a tool-footprint (a different discoverability property)** — a
lateral move, not a clear win, and a *breaking* one. This repo is its own counter-example: its
plans live under `docs/plans/` because the design `.md`s **are** documentation; forcing them (or
splitting the `.md` from its breakdown folder) into `.guardrails/` would fragment the plan-of-record.

The legitimate residual slice: a **consumer** repo (not dogfooding its own design docs) whose plan
`.md` is a throwaway may genuinely want a tidy `.guardrails/plans/<name>/` rather than scattering
folders. That want is real — but it is satisfied by **making `.guardrails/` a supported optional
home**, not by changing the default and imposing migration on everyone.

### Seams and contracts touched

None changed by the recommendation. If a *default change* were ever adopted, the seams would be:
`plan-breakdown` Step 0 / output-location logic (skill), SSOT §1 (plan folder layout prose), and
`guardrails.baseline` source-pointer resolution. Resume is git-trailer-based (SSOT §7.x) and
**location-independent**, so a relocation would not touch resume — a point in favor of *feasibility*
if the need ever becomes real, and a reason it can safely wait.

### Schema changes

**None.** Plan-folder location is a soft convention, not a schema field. No `02-schemas-and-contracts.md`
edit is required by the recommendation. (The additive "optional home" prose proposed below is a
convention note, not a contract change.)

## Devil's-advocate self-critique

**Strongest counter-argument — "the beside-the-`.md` model *is* the problem; a tool that scatters
artifacts looks unfinished, and refusing a home just re-litigates this later."** A reviewer could
argue the locality I call load-bearing is merely where `plan-breakdown` happens to write, that
resume is path-independent anyway, and that `.github/`/`.vscode/` prove users *expect* a home — so
declining is timid and defers an inevitable decision.

**Response.** Three points. (a) The coupling is real, not incidental: breakdown *regenerates* the
folder from the `.md` and `guardrails.baseline` records the tie — siblings make that visible with no
indirection; a home replaces it with a pointer. (b) The "scatter" is mostly illusory: transient
state is gitignored/out-of-tree, config is already `.guardrails/`-optional, so the only thing that
"scatters" is plan folders that are *intentionally* beside human-reviewed plans — a feature. (c) I
am **not** refusing the home — I am refusing to make it the *default* (which forces migration on the
dogfood repo and every existing user for a lateral gain). Documenting it as supported-optional
captures the upside for repos that want it, at the cost of one paragraph, and leaves a clean upgrade
path (name a v2 default-change bet) if real adoption friction ever appears. That is the YAGNI/KISS
call: adopt the cheap reversible half now, defer the expensive irreversible half until there is
evidence it is wanted.

**Second-order risk I accept:** three sibling dot-entries (`.guardrails/`, `.guardrails-staging/`,
`.guardrails-agent-io/`) is mildly confusing. I deliberately do **not** fold the transient two under
a real `.guardrails/` in v1 — that touches the containment-hook allowed-root logic and is more than
docs. If a home is ever formalized as a default, consolidating them under it is the natural v2
follow-up (noted below, not built now).

## Recommendation

**Option A — recommend-only.** Concretely:

1. **Do not change the `plan-breakdown` default.** Plan folders continue to be generated beside
   their source `.md`. (Rejects Option B.)
2. **Do not add a `guardrails init` command in v1.** Net-new setup ceremony for a marginal gain;
   strains invariant #6. (Rejects Option C. Revisit only with the same v2 trigger below.)
3. **Formalize `.guardrails/` as a *supported, optional* home** in the docs: a repo that wants one
   tidy footprint MAY place its plan folders (and `guardrails-patterns.md`, already so) under
   `.guardrails/`. Post-#266 this is purely aesthetic — every location, including `.claude/`-nested,
   already runs. No harness enforcement, no migration.
4. **Migration path: none required.** Existing `.claude/plans/` and `docs/plans/` users (including
   this repo's own `docs/plans/` dogfood tree) keep working untouched — the point of recommend-only.
5. **Not a named v2 bet.** Record a *revisit-if-adoption-friction* note in `03-roadmap.md`
   fast-follows: if real consumer feedback shows the beside-the-`.md` default causes friction,
   reopen as a proper v2 "default plan home + `guardrails init` + transient-folder consolidation"
   bet (with the migration + containment-hook work costed then). Until then, close #275 as
   *addressed: adopt as documented optional convention; default unchanged.*

**One-line verdict for the maintainer:** the folder was proposed to solve a write problem (#266
killed it) and a hygiene problem (#258's scaffold shipped); the surviving "give the tool a home"
case is real but weak and would break the load-bearing beside-the-plan locality if forced as a
default — so document `.guardrails/` as an optional home, change no behavior, and revisit a default
change only if adoption friction proves it wanted.

## Implementation handoff

Recommend-only — **no harness/schema work.** On user approval of this doc:

- **`guardrails-skill-author`** — add a short "Optional: a `.guardrails/` home" note to the
  `plan-breakdown` skill near Step 0 (where `guardrails-patterns.md`'s `.guardrails/` option is
  already documented), stating plan folders MAY live under `.guardrails/` and that location no
  longer affects runnability (post-#266). *filesTouched:* `.claude/skills/plan-breakdown/SKILL.md`
  (and the packed copy under `src/Guardrails.Cli/…/skills/` per the build's skill-bundling).
- **`guardrails-architect`** (me, on approval) — the two additive prose edits proposed below.

No sequencing dependency; both are additive doc notes. No `guardrails-harness-developer` or
`guardrails-test-author` involvement — there is no code or test surface.

## Proposed plan-document edits

*(Proposed, not applied — awaiting approval per the operating contract.)*

1. **`docs/plans/03-roadmap.md` → "Post-v1 fast-follows"** — add:
   > - **#275 `.guardrails/` folder convention — CLOSED as recommend-only.** Write-escape motive
   >   subsumed by #266 (agent `STATE_OUT`/`VERDICT_OUT` staged to `.guardrails-agent-io/`, SSOT §9.5);
   >   hygiene motive shipped by #258's plan-root `.gitignore` scaffold. Surviving "give the tool a
   >   home" case documented as an *optional* `.guardrails/` home (plan folders + `guardrails-patterns.md`);
   >   the `plan-breakdown` default (beside the source `.md`) is unchanged. **Revisit-if-friction:** a
   >   real consumer complaint about the default location reopens this as a v2 "default plan home +
   >   `guardrails init` + transient-folder consolidation" bet. Design: `docs/plans/14-guardrails-folder-convention.md`.

2. **`docs/plans/02-schemas-and-contracts.md` → §1 (Plan folder layout)** — append one sentence
   after the "generated next to its source markdown plan" line:
   > A repo that prefers one consolidated footprint MAY instead place plan folders under a
   > `.guardrails/` directory (the same optional home `guardrails-patterns.md` already documents);
   > post-#266 the location does not affect runnability, and the harness-scaffolded `.gitignore`
   > (§1) applies wherever the plan folder lives. The `plan-breakdown` default remains beside the
   > source `.md`.

*(No other SSOT change — no schema field is added or altered.)*

## Note on the design→draft-PR loop (#106)

This is a *design-of-record* doc, so the #106 loop nominally applies — but its conclusion is
recommend-only (near-zero behavior change), which sits at the light end where the loop's own
guidance exempts trivial/mechanical changes. **This task's boundaries explicitly forbid opening a
PR**, so I have not. Suggested handling: the user reviews this doc directly; if they want the
inline-review ceremony they can request a draft PR, otherwise — given it changes no behavior — it
can land directly on approval alongside the two additive prose edits above.
