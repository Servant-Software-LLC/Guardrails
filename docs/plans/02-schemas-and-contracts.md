# 02 — Schemas and Contracts (single source of truth)

Every schema and child-process contract in the Guardrails system is defined **here**.
The C# serializers (`src/Guardrails.Core`), the `plan-breakdown` and `guardrails-review`
skills, and the example plan folders all implement this document. If code or a skill
disagrees with this doc, one of them is wrong — fix in the same change.

JSON files are read with comments and trailing commas allowed (humans hand-edit them).
All harness writes are atomic (write temp file, then move over the target).

---

## 1. Plan folder layout

A *plan folder* is generated next to its source markdown plan (`<plan-name>.md` →
`<plan-name>/`):

```
plan-name/
├── guardrails.json              # run configuration (§2)
├── guardrails.baseline          # OPTIONAL committed breakdown manifest (§11)
├── diagram.md                   # OPTIONAL generated DAG diagram — non-authored (§10)
├── diagram.html                 # OPTIONAL interactive local viewer — non-authored (§10)
├── preflights/                  # OPTIONAL plan-level "Full Flight Checks" — run ONCE before the DAG (§4)
│   ├── 01-baseline-green.ps1     #   guardrail-shaped files (same parser as tasks/<id>/guardrails/)
│   └── 01-baseline-green.json    #   optional metadata sidecar (§4.1)
├── guardrails/                  # OPTIONAL plan-level terminal / integration gate — run ONCE at run end (§3.3/§4)
│   └── 01-full-suite.ps1         #   ≥1 real integration-set re-run for a multi-leaf/fan-in plan (GR2028)
├── state/
│   ├── seed.json                # OPTIONAL committed initial state (§6.1)
│   ├── state.json               # runtime merged state — harness-owned, gitignored
│   ├── run.json                 # run journal — harness-owned, gitignored (§7)
│   ├── guardrails-review.json   # OPTIONAL review marker — COMMITTED, planHash-keyed (§13)
│   └── merge-conflicts.log      # harness-owned, gitignored (§6.3)
├── logs/
│   ├── <runId>/<task-id>/attempt-N/   # per-attempt artifacts (§8) — divided by runId, sibling of state/
│   ├── <runId>/<task-id>/index.html   # static per-task log page — non-authored (§12.2/§12.3)
│   └── <runId>/index.html       # static log-site index — written on the fly during a run + by --export (§12.3)
└── tasks/
    └── <NN-verb-object>/        # task id = folder name, kebab-case, NN = topological hint
        ├── task.json            # task manifest (§3)
        ├── action.prompt.md     # or action.ps1 / action.sh / action.py / action.cmd / …
        ├── preflights/          # OPTIONAL task-level JIT dependency-delivery checks — run at taskBase before the action (§4)
        │   └── 01-dep-delivered.ps1  #   guardrail-shaped files (same parser as guardrails/)
        └── guardrails/
            ├── 01-build-passes.ps1        # deterministic guardrail (§4)
            ├── 01-build-passes.json       # optional metadata sidecar (§4.1)
            └── 02-review.prompt.md        # prompt guardrail with YAML frontmatter (§4.2)
```

Task ids are their folder names. The `NN-` prefix is a human-scanning hint only;
`dependsOn` is the truth for ordering.

**Two scopes, four folders (design-of-record 09-preflight-first-class).** `preflights/` and `guardrails/`
are first-class folders at TWO scopes. **Plan-level** `<plan>/preflights/` (the "Full Flight Checks") runs
ONCE before the DAG against the starting repo; `<plan>/guardrails/` (the terminal / integration gate) runs
ONCE at run end on the merged HEAD (§3.3). **Task-level** `tasks/<id>/preflights/` is a per-task JIT
dependency-delivery check run in the task's segment worktree before its action, the sibling of the
postcondition `tasks/<id>/guardrails/`. All four folders share **one** guardrail-file parser (§4) — they
differ only in WHERE they live and WHEN they run; every file opens with a `catches:` declaration, and a
malformed one (no `catches:`) is a hard load error (**GR2027**). The harness phases that RUN the three new
folders land in later deliverables; this change adds the loader/validator that parses and validates them.

**Workspace must be a git repository top-level.** Parallel execution never writes the user's
checkout. At run start the harness creates a **plan branch** `guardrails/<plan-name>` off the
user's current HEAD and a **harness-owned integration worktree** on it; this is the sole merge
target and the terminal-gate site for the run. Each task runs in a **segment worktree**: a linear
chain **reuses one** segment worktree passed along the chain; a fan-out **inherits one** chain and
**forks the rest** off the producer's committed tip; a fan-in **forks one** upstream and merges the
others in. `runId` lives in worktree directory names and commit trailers, **not** the branch name.
`guardrails validate` and a run pre-flight reject a non-git-top-level workspace (**`GR2015`**, a
FRESH code — the old plan-07 draft cited `GR2013`, which is **taken on `master`** by the live triad
`CaptureHashEscapesWorkspace`). The harness creates all worktrees under a **harness-owned root
outside the workspace** — default `<temp>/guardrails-worktrees/<workspace-hash>/<runId>/`,
overridable via `guardrails.json: worktreeRoot`. Worktrees + the plan branch are runtime state
(wiped by `--fresh`, pruned on resume; the integration worktree is reattached, not pruned). The
user's own working tree and branch are **read-only for the entire run**; the only optional write to
the user's branch is `--merge-on-success` (§5.3). A `runOnCurrentBranch` opt-in makes the plan
branch the current branch but still integrates via a harness-owned worktree, never the user's live
checkout.

The per-attempt log tree moves out of `state/` to a top-level `logs/` sibling, **divided by
`runId`** (`logs/<runId>/<task-id>/attempt-N/`), so logs are findable and a re-run's logs never
interleave with a prior run's. `state/` holds only harness-owned mutable run state; `logs/` is
append-only audit. `--fresh` clears `logs/` for the abandoned run.

---

## 2. `guardrails.json` (run configuration)

```jsonc
{
  "version": 1,                       // required; schema version of this file
  "maxParallelism": 3,                // default 3 in worktree mode (chain-reuse keeps a linear chain to ONE tree)
  "defaultRetries": 2,                // retries AFTER the first attempt; default 2
  "defaultTimeoutSeconds": 1800,      // per-attempt ceiling when nothing narrower applies
  "transientPauseBudgetSeconds": 14400,// cumulative wall-clock a task may spend PAUSED on transient infra limits (#115); default 14400 (4h); 0 disables pausing
  "maxCostUsd": 5.00,                 // OPTIONAL per-run cost ceiling, decimal USD; absent = no cap
  "guardrailMode": "failFast",        // "failFast" (default) | "runAll"
  "workspace": "..",                  // cwd for all child processes, relative to the plan dir
  "worktreeRoot": null,               // OPTIONAL; override the git-worktree root. null = <temp>/guardrails-worktrees/<hash>/<runId>/
  "runOnCurrentBranch": false,        // OPTIONAL; if true the plan branch IS the current branch (still integrated via a harness-owned worktree)
  "mergeOnSuccess": false,            // OPTIONAL; if true AND the whole run goes green, merge plan branch guardrails/<plan-name> into the user's original branch at run end (ff-only when possible; AI-merge is NOT used here)
  "triageAutoFile": false,            // OPTIONAL; opt-in auto-file of the needs-human triage GH issue (§9). Default OFF = draft into feedback.md only; gated behind a configured GH repo + token when on
  "preserveAttemptsForSalvage": true, // OPTIONAL; retry salvage (§3.2, issue #195). Default true. Preserves a rolled-back max-turns/output-cap attempt to a git ref instead of pure discard; set false to disable
  "interpreters": {                   // EXTENDS/OVERRIDES built-in defaults (§5.2)
    ".ps1": ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "{script}", "{args}"]
  },
  "promptRunners": {                  // §9
    "default": "claude",
    "claude": {
      "command": "claude",
      "permissionMode": "acceptEdits",
      "allowedTools": ["Read", "Edit", "Write", "Grep", "Glob", "Bash(dotnet *)"],
      "maxTurns": 50,
      "model": null,                  // null = CLI default
      "extraArgs": [],
      "maxOutputTokens": 64000,       // per-response output-token cap (#114); default 64000 (> Claude Code's 32000); GR2023 if <= 0
      "env": {},                      // extra env vars passed verbatim to the runner process (#114); user keys win last
      "guardrailOverrides": {         // tighter profile for verdict-only guardrail prompts
        "permissionMode": "default",
        "allowedTools": ["Read", "Grep", "Glob", "Write"],
        "maxTurns": 20
      }
    }
  }
}
```

<!-- canonical-schema:promptRunners — the `"promptRunners": { … }` block above (from its
     `"promptRunners":` line through its matching close, leading 2-space indent included) is the
     CANONICAL copy. `.claude/skills/plan-breakdown/references/schemas.md` mirrors it byte-for-byte
     between its `canonical-schema:promptRunners` sentinels (drift-tested). Edit here first. -->

- `workspace` is the repo/directory the plan operates ON (typically the folder that
  contains the plan folder). Children run with cwd = workspace; everything
  Guardrails-specific arrives via absolute paths in env vars (§5.1).
- `guardrailMode: failFast` stops at the first failing guardrail of a task attempt
  (guardrails are ordered cheapest-first by filename convention); `runAll` runs every
  guardrail and aggregates all failures into one feedback document.
- `maxCostUsd` caps total spend for the run. When the journal's cumulative cost — the sum of
  every attempt's `costUsd` (§7) — reaches or exceeds it, the harness stops launching new
  attempts: each not-yet-launched task settles `needs-human` (reason "cost cap reached") and
  its transitive dependents `blocked`, via the same halt path as any other needs-human task. An
  attempt already in flight is never interrupted — the cap gates new launches, not running work.
  Absent ⇒ no cap. A present non-positive value is a validation error (GR2012).
- `worktreeRoot` overrides where the integration + segment worktrees are created. Each task's child
  processes run with cwd = its segment worktree; the integration worktree (plan branch
  `guardrails/<plan-name>`) is written only by the harness's integration (§5.3).
- `runOnCurrentBranch` (default `false`) makes the plan branch the current branch instead of a fresh
  `guardrails/<plan-name>`; the harness still integrates via a harness-owned worktree, never the
  user's live checkout. **Pre-flight:** if `runOnCurrentBranch` is set AND the current branch has
  uncommitted changes, the harness PROMPTS for explicit permission at run start (interactive) or
  REFUSES and halts (non-interactive, unless an explicit `--yes`/auto-confirm is given) — because the
  end-of-run integration merges back into the current branch and a dirty tree invites merge
  complications. **GR2016** (warning): a deep `worktreeRoot` + deep source tree risks exceeding
  Windows MAX_PATH (260 chars); document `core.longpaths` as the mitigation.
- `mergeOnSuccess` (default `false`; CLI `--merge-on-success` overrides) opts into end-of-run
  delivery of the plan branch into the user's original branch. **AI-merge is withheld at this
  boundary** — a conflict, a failed post-merge re-verify, or a dirty user tree halts to `needs-human`
  with the plan branch intact; never a force-overwrite, never an AI auto-resolve of the user's commits.
- `maxParallelism` defaults to **3** because chain-reuse keeps a linear chain to one worktree; the
  peak tree count is the DAG's max antichain width + the integration worktree. Drop to 2 on a
  disk-constrained box; raise on a fast/large `worktreeRoot` volume.
- `transientPauseBudgetSeconds` (default `14400`, i.e. 4h — a long unattended/overnight run must ride
  out a multi-hour outage or usage-limit window without settling `needs-human`, issue #189) is the
  cumulative wall-clock a single task may spend
  **paused** on transient, retryable infrastructure conditions (HTTP 429/503/529, "overloaded", a
  usage/session/rate limit from the runner — issue #115). A transient signal does **NOT** consume the
  retry budget: the harness backs off (bounded exponential, 2s→…→60s cap, honoring a parsed reset hint
  for display) and re-runs the **same** attempt, surfacing a distinct `PromptPaused` observer event
  (CLI: a `paused` row, not a failure). A transient pause that clears is **never journaled** —
  observe-only. This is the named bound on **"a rate limit is never `needs-human`"**: only if the
  limit fails to clear within this whole-task budget does the task settle `needs-human` with the
  distinct `rate-limited` outcome (§7) and a "re-run later" reason. `0` disables pausing (a transient
  signal is then a normal action failure).
- `promptRunners.<name>.maxOutputTokens` (default `64000`) caps the runner's per-response output
  budget (issue #114). The default sits **above** Claude Code's own 32 000 default so a well-formed
  single-response task is not blocked by a cap the harness never used to configure. The runner CLASS
  translates it into the CLI's env var (`CLAUDE_CODE_MAX_OUTPUT_TOKENS` for `claude`) — the env-var
  NAME is **quarantined in the runner**, never in this schema or the §5.1 `GUARDRAILS_*` set. A
  non-positive value (base or via `guardrailOverrides`) is a validation error (**GR2023**). When a
  response still exceeds the cap, the runner detects it and the harness surfaces a distinct
  `output-cap` outcome (§7) with actionable retry feedback ("write the file incrementally / split"),
  not a generic action failure.
- `promptRunners.<name>.env` (default `{}`) passes extra environment variables verbatim to the runner
  process (issue #114) — a general passthrough for runner/provider knobs the harness does not model.
  It overlays the harness `GUARDRAILS_*` env; a user-set key **wins last** (it is authoritative, and
  may even override the translated `maxOutputTokens` cap). `guardrailOverrides` may narrow both
  `maxOutputTokens` and `env` for the verifier profile.
- `preserveAttemptsForSalvage` (default `true`) — **retry salvage** (issue #195, worktree mode only; a
  no-op in serial mode, which has no segment to preserve). See §3.2 for the full mechanism; in brief:
  a **non-final** attempt that ends `max-turns` or `output-cap` — the two NON-LOGIC budget-exhaustion
  outcomes — has its full working tree (including uncommitted writes) committed to
  `refs/guardrails/<taskId>/attempt-<N>` immediately BEFORE the existing F2 `git reset --hard
  <taskBase> + git clean -fd` rollback discards it. The next attempt still starts from the clean
  `taskBase` (unchanged, deterministic) — only the RETRY FEEDBACK changes: it names the ref, a `git
  diff --stat <taskBase> <ref>` summary of what that attempt changed, and instructs the agent to
  `git checkout <ref> -- <path>` the good parts rather than re-deriving everything from scratch.
  Deliberately scoped to non-logic outcomes: a `guardrail-failed` rollback is **never** preserved by
  this flag (the code may be genuinely wrong, so silently carrying it forward is the wrong default).
  Set `false` to disable salvage entirely for a plan.

## 3. `tasks/<id>/task.json`

```jsonc
{
  "description": "Implement the --stats flag",   // required, one line, human + feedback use
  "stableId": "k3f9a1",        // optional; stable task identity for the regeneration merge (§11)
                               //   format ^[a-z0-9][a-z0-9._-]*$ (GR2011); unique (GR2010)
  "dependsOn": ["01-author-stats-tests"],        // required (may be []); task ids
  // NOTE: "integrationGate": true is RETIRED — the terminal gate is now the <plan>/guardrails/ folder (§3.3).
  //       Still declaring it is a hard validation error (GR2029). Do NOT add this key to new task.json.
  "writeScope": ["src/Foo/"],  // optional; the deterministic write-scope check (§3.4). Absent ⇒ NO check.
                               //   every path the action's post-action diff (staged worktree vs <taskBase>)
                               //   adds/modifies/deletes/renames must be IN scope, or the task fails and
                               //   retries with feedback after a SCOPED REVERT of the out-of-scope paths
                               //   (in-scope WIP preserved). Renames = paired D+A (both in scope). A vacuous
                               //   "**" / bare top-level dir is a granularity smell.
  "stagingOutputs": [                                // optional; autonomous .claude/ delivery (§3.5). Absent ⇒ none.
    { "from": "skill/**", "to": ".claude/skills/foo/" }  // action writes <from> under GUARDRAILS_STAGING_DIR;
  ],                                                 //   harness MOVES it to <to> after action, before guardrails
  "retries": 3,                // optional; overrides defaultRetries
  "timeoutSeconds": 3600,      // optional; whole-attempt ceiling (action + guardrails)
  "action": {                  // OPTIONAL — omit to use convention discovery:
                               //   exactly ONE file named action.* in the task folder;
                               //   zero or multiple action.* files = validation error
    "path": "action.prompt.md",      // relative to task dir; kind derived from extension
    "args": [],                      // deterministic actions only
    "runner": "claude",              // prompt actions only; default = promptRunners.default
    "maxTurns": 80,                  // prompt actions only
    "model": null,                   // prompt actions only; null = inherit from the runner's default model
    "timeoutSeconds": 2400,          // narrower than task timeout
    "workingDirectory": null,        // overrides config workspace (rare)
    "env": { "MY_VAR": "value" }     // extra env vars for this action's process
  }
}
```

**Action kind by extension**: `.prompt.md` → prompt; anything else → script/executable
resolved through the interpreter map (§5.2). A task **must** have an action and
**at least one guardrail** — zero guardrails is a validation **error** (a task that
can't be verified has no business in the DAG).

`stableId` is an **optional** identity that survives renumbering and slug edits across
regenerations — the key the merge (§11) uses to recognize "this is the same task, slightly
altered" versus "this is a new task". It is reserved for that merge and the runtime does not
yet consume it, but because the merge keys identity on it, `validate` **does** enforce two rules
on any declared `stableId`: it must be **unique** across tasks (a duplicate is a `GR2010` error —
almost always a copy-paste slip), and it must match `^[a-z0-9][a-z0-9._-]*$` (lowercase
alphanumerics, optionally with `.` `_` `-`; a `GR2011` error otherwise). The format is reserved so
a real id can never collide with the merge's synthetic `folder:<name>` identity (the colon is
disallowed). `validate` does not *require* one. Absent ⇒ task identity falls back to the folder
name — see §11.3 for why minting one is still recommended.

`action.model` (issue #200) is an **optional** per-task override of which model runs this task's
prompt action — mirrors `action.maxTurns` exactly (same shape, same "task.json wins" precedence). The
full resolution order, evaluated once per attempt: **`task.json action.model`** (if set) **>
`promptRunners.<name>.model`** (if set) **> the CLI's own default** (no hardcoded fallback — if
neither is set, the runner is simply never passed a `--model` flag). A present `model` at either site
must be a real-looking value — non-empty, no leading/trailing/embedded whitespace or control
characters — or `validate` rejects it (`GR2030`); a `null`/absent value is always fine and means "no
override here". The resolved value is also what `run.json`'s per-attempt provenance records (§7) —
provenance never lags behind what actually ran.

*(Former §3.1/§3.1.1 — the `captureHashes`/`restoreOnRetry` triad — are **removed in this change**,
along with the harness `CapturedFileStore`/`FileHashCapture`/`RestoreAncestorCaptures`/`WorkspaceLock`
and the GR2013/GR2014 triad diagnostic meanings. Test files are now protected by (i) physical
worktree isolation and (ii) the §3.4 write-scope check: an implementation task's `writeScope` excludes
the test files, so an edit to them fails the deterministic check.)*

### 3.2 Worktree task semantics

The harness creates one integration worktree per run (plan branch `guardrails/<plan-name>`) — the
sole merge target. Each task runs in a **segment worktree**: a linear chain reuses one segment
worktree (the downstream task commits on top of the upstream's tip in the SAME tree — no inter-hop
merge, no inter-hop re-verify, sound because no union is formed); a fan-out **inherits one** chain
(the longest-downstream successor reuses the producer's segment worktree directory; ordinal-id
tiebreak) and **forks the rest** off the producer's **recorded** committed sha (never the live
segment-branch tip, which the inheritor may have advanced); a **fan-in** task forks a fresh segment
off the **plan-branch tip**, which already contains every producer's integrated work (the producers'
own settles unioned it onto the plan branch), so the fan-in sees the merged tree without a separate
private merge. *(A private pre-merge worktree — `CreateFanIn`/`FanInHandle` — was **removed**; the
plan-branch union is the sole fan-in mechanism. See plan 08 `topology-wiring-design.md` Decision F.)* A failed attempt does NOT discard the worktree — the harness `git reset --hard
<taskBase> + git clean -fd` (preserving every upstream/sibling commit in the tree; `taskBase` is the
task's start commit, distinct from the plan-branch `preHead`). A task that depends on another reads
the producer's MERGED outputs (its worktree descends from the producer's committed tip). No
cross-task `actionExitCode` channel exists. The user's checkout is never written; the plan branch's
trailer-bearing commits (plain FF'd commits AND merge commits) are the durable resume record (§7).
At run end the harness sweeps the segment worktree directory of every task that settled **green** (its
work is durable on the plan branch, so the directory is pure waste — the direct fix for **#126**),
then prunes the registrations; a **non-green** (needs-human/failed/blocked) task's worktree is left in
place as the fix/resume inspection surface, and the integration worktree is never swept. A cancelled
run skips the sweep entirely (its in-flight worktrees are reclaimed by the next run's resume prune).

**Retry salvage (issue #195) — preserve, don't just discard, a non-final rollback for NON-LOGIC
outcomes.** The F2 rollback above (`git reset --hard <taskBase> + git clean -fd`) is unconditional —
EVERY non-final worktree attempt resets, regardless of failure kind (§7's `WorktreeWillReset`
predicate). For the two **non-logic budget-exhaustion** outcomes — `max-turns` and `output-cap` — a
`max-turns`/`output-cap` termination is NOT a logic failure: the attempt's partial work is usually
CORRECT-BUT-INCOMPLETE (the agent was making real progress and simply ran out of budget), not wrong.
Discarding it outright and starting the next attempt from scratch is expensive and slow, especially
across several rolled-back attempts. When `preserveAttemptsForSalvage` (§2, default `true`) is on and
the task runs in worktree mode, the harness — immediately BEFORE the F2 reset — commits the attempt's
**current full working-tree state** (including uncommitted writes) to a per-attempt ref:

```
refs/guardrails/<taskId>/attempt-<N>
```

using a throwaway index (`GIT_INDEX_FILE`) so the segment's real staged/unstaged state is never
disturbed — this is a side-channel snapshot, never a real commit on the segment branch/HEAD. **The
next attempt still starts from the clean `taskBase`** — deterministic, no half-broken state as the
base; this does NOT change. What changes is the **retry feedback**: `feedback.md` (§8) gains a
"Prior attempt work is salvageable" section naming the ref, a `git diff --stat <taskBase> <ref>`
summary of exactly what that attempt changed, and an explicit instruction to `git checkout <ref> --
<path>` the files that are correct and re-author only what is incomplete or wrong — reviewing and
selectively adopting, not blindly restoring every file. **Salvaged files remain subject to the task's
declared `writeScope`** (§3.4) exactly like any other write: the write-scope check runs a retrospective
`git diff` on the FINAL state regardless of how it got there (fresh authorship or a `git checkout <ref>
--`), so an out-of-scope file pulled in from a salvage ref is caught and scoped-reverted identically to
a freshly-written out-of-scope file.

**Scope guard — restricted to non-logic outcomes by default.** A `guardrail-failed` attempt's code may
be genuinely WRONG, so it is **never** preserved by this mechanism regardless of the config flag — only
`max-turns` and `output-cap` participate. `timeout` also does **not** participate (a generic budget
signal the issue did not scope salvage to); its existing mode-aware rollback disclosure (§7, issue
#167) is unchanged. Preservation is additionally best-effort: a git failure while preserving degrades
to no salvage (the feedback falls back to its pre-#195 wording) rather than failing the attempt or
altering the unconditional F2 reset.

**Pruning.** A task's salvage refs are bookkeeping for THAT task's own retry loop, not a permanent
record, so they are pruned in the two places other per-task/per-run git cleanup already happens: (1)
the moment a task's FINAL settle is `succeeded` (alongside the Scheduler's existing green-worktree
sweep) — its prior rolled-back attempts have served their purpose; (2) a full `--fresh` reset (alongside
the existing stale segment/fork branch prune in `RunReset.Fresh`), which sweeps every salvage ref in the
repo regardless of task, since a fresh run's tasks get fresh attempt numbers and any survivor would be
orphaned bookkeeping. A task that never succeeds (exhausts to `needs-human`) keeps its salvage refs
until the next `--fresh` — they remain available for a human to inspect during triage.

### 3.3 Terminal integration gate — the `<plan>/guardrails/` folder (was the `integrationGate` task kind)

The terminal whole-repo integration gate is the final soundness boundary, run once on the fully merged
plan-branch HEAD after all other tasks succeed. It re-runs the run's **integration set** (§4.3) — typically
the whole-repo build and the full test suite — as the whole-repo soundness boundary for FF chains and
AI-resolved unions.

**The gate is now a first-class FOLDER, `<plan>/guardrails/`, NOT a task (design-of-record
09-preflight-first-class).** The terminal checks live in the plan-level `<plan>/guardrails/` folder (§1),
evaluated once at run end by the terminal phase. The old modelling — a no-op END task carrying
`integrationGate: true` whose guardrails were the integration set — is **retired**.

**`integrationGate` task kind + GR2017 — RETIRED (no coexistence window).** The `integrationGate: true`
task kind and **GR2017** (the old "a multi-leaf/fan-in plan must declare exactly one `integrationGate: true`
sink" rule) are gone. There is no migration window: a plan that STILL declares `integrationGate: true` is a
**hard validation error — GR2029** (honest-over-silent: the stale key is caught at validate time, never
silently ignored, UNCONDITIONALLY — a plan can therefore never carry the legacy key AND a
`<plan>/guardrails/` folder at once). The harness keeps a `TaskNode.IntegrationGate` model field only so
the validator can DETECT and reject the legacy key. The Scheduler's own legacy terminal-gate run (the
pre-deliverable-4 per-task `scope: "integration"` sink-task path) still exists and still reads it, but now
SUPERSEDED (never both) by the terminal phase (deliverable 4, §7.1) whenever a plan declares a
`<plan>/guardrails/` folder.

**GR2018's content teeth — RE-HOMED onto the folder as GR2028, NOT retired, NOT weakened.** The old GR2018
required the `integrationGate` sink to carry ≥1 `scope: "integration"` guardrail ("a gate that verifies
nothing"). That **content obligation moves to the folder**, with its teeth intact: **GR2028** (error) — a
multi-leaf or fan-in plan MUST have a `<plan>/guardrails/` folder carrying **≥1 deterministic check that
ACTUALLY re-runs the integration set** (a whole-repo build / full suite / a union invariant). It is
deliberately NOT weakened to "the folder is non-empty": an empty folder fails, and so does a folder holding
only a tautological `exit 0` file that certifies nothing — the exact failure GR2018 exists to prevent. The
check is by **content**, not presence. A single linear chain (one leaf, no fan-in) forms no union and is
exempt, and — matching the retired GR2017/GR2018's exact firing conditions — the rule applies only in
**worktree mode** (`maxParallelism > 1`); a serial run merges no parallel branches, so there is no
merged-HEAD union for a terminal gate to certify. The "counts toward the terminal gate" marker is **folder
membership** (a folder-scoped equivalent of the §4.3 tag); the surviving obligation is the ≥1-real-re-run.

**Both forms of "a real integration-set re-run" are recognized, not just build/test.** GR2028's content
check (`PlanValidator.ReRunsIntegrationSet`) accepts a `<plan>/guardrails/` script matching EITHER: (1) a
recognized whole-repo build/test/suite command across common ecosystems (`dotnet test`/`dotnet build`,
`npm test`, `pytest`, `make`, `git diff --check`, …) actually **invoked**, OR (2) a genuine **union
invariant** — a check for git conflict markers (`<<<<<<<`/`=======`/`>>>>>>>`) in the merged bytes, the
deterministic verdict that a union integrated cleanly. Form (2) exists for plans with no build/test tool to
invoke at all (e.g. a portable, zero-toolchain demo like `examples/parallel-hello`) whose only honest
integration content is exactly this shape.

The two forms are matched at **different rigor by design (issue #207)**. A comment that merely names a marker
or a build command never counts under either — whole-line comments are stripped first (`StripCommentLines`).
Beyond that:
- **Form (1) requires an INVOCATION shape, not a bare keyword anywhere on a non-comment line.** A line that
  only *mentions* a build command inside a string — `echo "reminder: dotnet test should pass"` — invokes
  nothing and is **rejected**. The command must be the **leading command word of a pipeline/statement
  segment** (a real invocation at a statement position) and must **not** be the argument of an output builtin
  (`echo`/`printf`/`print`/`Write-Output`/…). Quoted-string literals are stripped per line first, so a keyword
  inside a quote never counts. A piped/chained real invocation (`dotnet build && dotnet test 2>&1 | tee log`)
  still counts — the command sits at a statement position within the pipeline.
- **Form (2) stays a literal token match on the comment-stripped (not quote-stripped) body** — a genuine
  conflict-marker check often carries the 7-char token in a quoted string (`grep -q '<<<<<<<'`), and no
  legitimate reason exists to write that exact sequence other than detecting it, so it remains ungameable.

**`scope: "integration"` — KEPT as the §4.3 per-union tag (unchanged).** Only the terminal-SINK obligation
moved to the folder. The `scope: "integration"` tag still exists and still drives the **per-union re-verify**
(§4.3) at every intermediate fan-in / non-FF integration point during the run — that mechanism is unchanged.
The terminal `<plan>/guardrails/` folder (run once, last, declared by folder membership) and the per-union
integration set (run at every union, declared by the tag) are two declarations with one shared spirit, not
one object; the terminal folder's checks are typically a superset-or-equal of the per-union set.

**Malformed declaration in any of the four folders — GR2027.** Every guardrail-shaped file in
`<plan>/preflights/`, `<plan>/guardrails/`, `tasks/<id>/preflights/`, and `tasks/<id>/guardrails/` must open
with a `catches:` declaration (§4). A file that does not is a hard load error, **GR2027** — the canonical
per-folder malformed-declaration diagnostic for the four-folder model.

**Merge-collision attribution on gate failure (issue #175, ported to the terminal phase by #205).** When the
terminal gate fails on the final merged HEAD, the failure is surfaced as a terminal halt (exit 2,
`planGuardrails.status = plan-guardrail-failed`). The attribution is a property of the gate failure, not of
where the gate lives, so it applies identically whichever terminal path fires — the legacy per-task
`integrationGate` sink (`Scheduler.WithTerminalGateFailure`) and the four-folder terminal phase
(`PlanGuardrailPhase`) both call the **shared `WriteScope.OverlappingWriteScopeHint` helper**. A gate failure
(typically the whole-repo build/test) is frequently a **merge collision**: two tasks with **overlapping
`writeScope`** on a shared file both wrote new content there, and an AI/3-way merge silently kept both — a
semantic duplicate (e.g. a duplicate class/member) with **no textual conflict marker**, catchable only at the
build gate. The harness does NOT (and cannot generically) detect the semantic duplicate — that is the build
guardrail's job, and the union-guardrail prevention is authoring-side (§4.3 "Accepted residual"). What the
harness DOES is **attribution**: the gate-failure diagnosis enumerates every task pair whose `writeScope`s
overlap and names the shared path(s), so a human immediately sees *"this looks like a merge collision between
task A and task B on `<file>`"* rather than a bare build error. In the terminal phase the hint is journaled to
the OPTIONAL `planGuardrails.collisionHint` field (§7) and echoed in the `run` command's terminal-halt block.
The hint is advisory and structural — derived PURELY from the `writeScope`-overlap topology (never the
compiler error text / a CS-code), and **added only when two or more `writeScope`s overlap** (nothing is
appended for a plan with disjoint scopes).

### 3.4 Write-scope check (`writeScope`)

`writeScope` is an optional list of **workspace-relative path prefixes / globs** declaring the
surface a task is permitted to add/modify/delete/rename. It drives a **deterministic harness check**:
after the task's action and **before** its own `guardrails/`, the harness inspects the action's
**uncommitted** writes in the segment worktree and asserts every changed path satisfies
`IsInScope(path, writeScope)`. Because the check runs **before** the segment commit, the action's
output is not yet on `segmentHEAD` (HEAD == `taskBase` at this point); a `taskBase..segmentHEAD`
commit diff would be empty and pass vacuously. The harness therefore stages the worktree
(`git add -A`) and diffs the **index against `taskBase`**
(`git diff --cached --name-status --no-renames <taskBase>`), which surfaces modified, deleted, AND
new/untracked paths. Staging is not a content rewrite, and the Scheduler stages + commits the same
tree on the pass path anyway. A violation is a guardrail-class failure: the harness performs a
**scoped revert** that undoes ONLY the out-of-scope paths — an out-of-scope MODIFY/DELETE is restored
with `git checkout <taskBase> -- <path>`, a newly-ADDED out-of-scope file is removed with
`git rm -f -- <path>` — leaving same-attempt **in-scope WIP intact**, then retries with feedback
naming the out-of-scope paths (eventual `needs-human`). **Diagnostic (issue #253):** `git add -A`
sweeps up EVERY untracked file present in the segment worktree at check time, not just ones the
agent's own tool calls wrote — an environmental leak (a stray build/test artifact, an interrupted
process's leftover) can therefore surface as an unattributable "write-scope violation" with no trace
in the agent's own transcript. Each offending path in `WriteScopeCheckResult.OffendingPaths` (a
`WriteScopeOffense`, not a bare string) carries the raw `git diff --name-status` change-status letter
(`A`/`M`/`D`; `?` for the WS_2 git-error sentinel) so a human debugging a later `needs-human` can tell
a brand-new/untracked file with no history at `taskBase` (suspicious/unattributable) apart from a
modification/deletion of a file that genuinely existed before the attempt (far more likely a real
agent mistake). An `A` offense also carries a best-effort forensic `Preview` (size + a short text
snippet) captured DURING the check, before the scoped revert deletes the file — otherwise the file
is simply gone with no trace by the time anyone reads the retry feedback. Both are threaded into
`RetryPolicy.ForWriteScopeViolation`'s feedback text.
**Absent ⇒ no check** (the off-switch — a task that can't be confidently scoped omits the field and
is reported as a broad surface, never given a vacuous `**`). **Renames** are NOT detected via git
`-M`; a rename presents as a paired **D + A**, and **both** paths must be in scope. **Deletions:**
the deleted path must be in scope. The declared scope is also injected into the action prompt
(advisory) — the deterministic check is the gate. `validate` rejects a scope entry that escapes the
workspace (**GR2019**, error) and warns on a vacuous/over-broad scope (**GR2020**, warning;
`plan-breakdown` should omit rather than emit a vacuous scope). **TDD test-protection:** a
test-author task owns its test files in `writeScope`; the implementation task's `writeScope` EXCLUDES
the test files, so the check deterministically enforces "the implementation may not write the tests"
(the replacement for the `captureHashes`/`tests-untouched`/`restoreOnRetry` triad **that this same
change deletes** — the triad was live on `master`). The matcher (`IsInScope`/`Overlaps`/segment-matcher)
is specified in full in plan 08 §2.1 (glob grammar, the 27-row truth table) and carries the §2.2
proof harness (the 27-row table + the two fuzz properties: membership-implies-overlap AND
`Overlaps`-completeness). It is read-only, so a matcher bug can only false-red or miss-catch ONE
task's own verdict — never write another task's files; `Overlaps` (the scheduler hint) retains
cross-task reach and keeps the full fuzz rigor.

When a task declares `stagingOutputs` (§3.5), the write-scope check runs on the **post-move**
surface: it gates the real `.claude/` destination paths (which the task's `writeScope` must
authorize), not the pre-move staging writes — the surface the check protects (what reaches the
commit) is unchanged and still fully gated.

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
zero changed paths because it is deleted before the diff. The `to` destinations are also
**implicitly in-scope** for the write-scope check (a staging task need not list its `.claude/`
destinations in `writeScope` as well); an *undeclared* `.claude/` write still fails the check.
**Subsumes #85:** the `.claude/` block is by path pattern, so no permission-config value unblocks a
worktree `.claude/` write; `stagingOutputs` is the supported autonomous path.

## 4. Guardrails

Files under `tasks/<id>/guardrails/`, executed in filename sort order (**ordinal**,
locale-independent — task folders sort the same way). Convention: order
cheapest-first (`01-exists`, `02-builds`, `03-tests`, `04-review`).

A guardrail's **name** (used in the journal, feedback, and UI) is its filename minus
the extension, with `.prompt.md` stripped as a whole:
`02-tone-is-friendly.prompt.md` → `02-tone-is-friendly`; `01-build.ps1` → `01-build`.
Every guardrail file **opens with a `catches:` comment** stating what wrong
implementation it catches (script comment or frontmatter field) — if you can't
write that sentence, the guardrail is decorative and should be deleted.

**Pass/fail contract (deterministic)**: exit code `0` = pass, non-zero = fail.
On failure, print a one-line *actionable* reason to stdout — that text becomes the
retry feedback ("greeting.txt missing 'Hello'" beats "FAIL").

### 4.1 Metadata sidecar (deterministic guardrails, optional)

`<guardrail-basename>.json` next to the script:

```jsonc
{
  "description": "Solution builds clean",
  "args": ["--configuration", "Release"],
  "timeoutSeconds": 600
}
```

### 4.2 Prompt guardrails (`*.prompt.md`)

YAML frontmatter (all keys optional) + prompt body:

```markdown
---
description: LLM review of the report tone
runner: claude
maxTurns: 20
timeoutSeconds: 900
---
You are a verifier. Read the report at out/report.md and judge ONLY whether ...
```

**Verdict contract**: a prompt guardrail MUST end by writing

```json
{ "pass": false, "reason": "Report never names the failing task." }
```

to the file at `GUARDRAILS_VERDICT_OUT`. Missing file, invalid JSON, or missing
`pass` ⇒ the guardrail **fails** with reason "guardrail produced no valid verdict".
CLI exit codes are never used for semantic pass/fail of prompt guardrails — exit
codes only distinguish "ran" from "crashed".

### 4.3 Guardrail scope (`scope: "integration" | "local"`)

A guardrail declares an optional `scope` (deterministic sidecar key §4.1, or prompt frontmatter
§4.2): `"local"` (default) or `"integration"`. The run's **integration-guardrail set** = the union
of all `scope: "integration"` guardrails across the plan (typically the whole-repo build + the
whole test suite). At **every union point** (a fan-in or a non-FF plan-branch integration, §5.3), on
the merged bytes, BEFORE the merge commit and BEFORE any downstream action, the harness re-runs **the
run's integration-guardrail set** (via the attempt-decoupled re-verify seam). This is the **complete
v1 union re-verify contract**: one set, run uniformly at every union and again on the final merged
HEAD by the terminal `<plan>/guardrails/` folder (§3.3). The terminal gate and the per-union re-verify are
one mechanism running the **same** set at two scopes. There is no per-task or per-colliding-sibling
guardrail selection at a union in v1 — the integration set is the whole re-verify.

**Residual the v1 integration-set-only contract accepts (the B-3 three-net residual).** Because the
union re-verify runs only `scope: "integration"` guardrails — not a colliding sibling's full
`local` set — an AI-merge that drops a colliding sibling's source hunk while leaving the sibling's
test file textually untouched is NOT caught by any *local* guardrail at the union. v1 catches such a
drop by **three nets, all integration-scoped or global**: (1) the **disjoint-scope CHECK** that makes
two tasks writing the same file a flagged plan-shape problem, so genuine colliding siblings are rare
by construction; (2) the **integration-guardrail set** (the whole-repo build + whole suite) re-run at
the union, which catches any drop that breaks a build or an integration-scoped test; and (3) the
**terminal whole-repo gate** on the final HEAD. A purely-`local` regression hidden inside a cleanly
re-merged file — invisible to all three nets — is an **accepted v1 residual**, tracked by **#132**;
re-running colliding siblings' full `local` sets at unions (the superseded three-part union model) is
deferred, not adopted.

> **Accepted residual (#132) — integration-set-only union re-verify.**
> - **WHY integration-set-only.** The union re-verify runs on **arbitrary union bytes outside any
>   attempt lifecycle**, so it can re-run only guardrails that are sound in that context — the
>   `scope:"integration"` set (whole-repo build, whole suite). A colliding sibling's per-attempt
>   `local` guardrails would **false-fail** at the union: fragment-readers checking
>   `GUARDRAILS_STATE_FRAGMENT` (no fragment exists at a union), anti-tautology
>   `tests-fail-on-current-code` (inverted once the sibling's code is merged), and guardrails for
>   not-yet-run downstream tasks. Running the `local` set at the union is therefore **withheld by
>   design** — it is exactly the false-failure removed this session ("Fix A").
> - **RESIDUAL.** A hunk an AI-merge silently drops on a **shared file** (overlapping `writeScope`s of
>   colliding siblings) is re-verified at the union ONLY by an integration-scoped guardrail. A drop
>   catchable **solely** by a sibling's `local` guardrail is NOT re-verified at the union (it surfaces
>   at the terminal `<plan>/guardrails/` gate, or not at all).
> - **MITIGATION (authoring, not runtime).** The well-authored plan covers the residual with a
>   `scope:"integration"` guardrail on the integration / fan-in task asserting the shared file's
>   **union invariant** (every colliding sibling's contribution survives the merge — union-safe per
>   §4.3 above), as the texttools showcase does with `components-union-verified`. `plan-breakdown`
>   emits such a union-guardrail when it generates overlapping `writeScope`s; `guardrails-review` emits
>   a **WEAK** finding when colliding writeScopes carry no integration union-guardrail (its
>   "overlapping-writeScope union-guardrail" structural probe). This is the chosen v1 resolution:
>   convert the silent gap into a **visible authoring-time nudge**; the runtime contract is unchanged.

Because the re-verify runs on arbitrary union bytes outside any attempt
lifecycle, it uses a **public attempt-decoupled re-verify seam** (NOT the attempt-bound internal
guardrail runner). The re-verify child process runs with cwd = the integration worktree and
`GUARDRAILS_WORKSPACE` set to that same path (#124) — so a guardrail reading `$GUARDRAILS_WORKSPACE`
resolves files identically in-attempt and at re-verify; the `GUARDRAILS_ACTION_*` attempt-lifecycle
vars stay deliberately absent (there is no action at a union point). `plan-breakdown` marks a
**union-safe conditional invariant** (the conflict-marker / overlapping-writeScope union-guardrail)
`scope: "integration"` — NOT the full build/test, which are terminal postconditions kept `local`
(#165, the §4.3 terminal-postcondition anti-pattern); `guardrails-review` flags an integration-sensitive
plan whose integration set is missing or too thin to be the union's whole re-verify (BLOCKER).

### 4.4 Stale `covers-key-behaviors` coverage (validated, GR2026 — warning)

The `covers-key-behaviors` archetype (`plan-breakdown` guardrail catalogue) greps the one test file a
task authors for a handful of distinctive literal terms drawn from the task's action prompt — one
`if ($content -match "<token>")` per behavior — so a single trivially-failing stub cannot satisfy a
multi-behavior prompt. When the action prompt is edited mid-lifecycle (a scenario removed, scope
narrowed) but its coverage guardrail is **not** updated to match, the guardrail keeps requiring a token
the prompt no longer mentions: a correct implementation following the prompt can never satisfy it, so
the task gets "need `<token>`" retry feedback that contradicts the prompt and dead-ends at
`needs-human` on **every** attempt (issue #157).

`guardrails validate` surfaces this drift as a **WARNING (GR2026)**, never an error: for each task it
locates the covers-key-behaviors-style script guardrail, extracts its required tokens, and
cross-references each against the SAME task's action prompt text with a **case-insensitive whole-word
keyword-presence** check; a token absent from the prompt is reported as stale (naming the token and the
task). It is a **heuristic**, deliberately conservative to protect the zero-false-positive spirit even
for a warning:

- **Archetype recognition** fires only when confident — either the issue's canonical `$hits -lt N`
  threshold is present, OR the guardrail carries the canonical `covers-key-behaviors` file name (the
  per-term `-notmatch … exit 1` form the catalogue emits, which has no `$hits` counter). Anything else
  is not treated as the archetype.
- **Token extraction** takes only a quoted string literal on the right of a `-match`/`-notmatch`
  against the scanned content variable (`$content`/`$tn`/`$code`/`$text`/`$file`), and only when the
  literal is a **clear keyword** — alphanumerics plus `. _ -`, ≥3 chars, no regex metacharacters. A
  regex-shaped literal (anchors, classes, alternations, escapes) is skipped: it cannot be confidently
  keyword-matched against prose.
- **Polarity — POSITIVE (require-present) tokens only (issue #177).** GR2026 applies to coverage
  tokens the prompt is *expected to mention* because the guardrail requires them to be **present** in
  the authored file. A guardrail can instead make a **negative assertion** — fail when a keyword is
  present (`if ($content -match "Foo") { … exit 1 }`) — whose keyword is *intentionally absent* from
  the prompt; flagging that as stale is a false positive. Each match-line is therefore classified by
  the polarity that makes its `exit <non-zero>` fire: a `-notmatch … exit` block (fail-on-absent) and
  a `-match … $hits++` counting block are **require-present** (kept); a `-match … exit` block
  (fail-on-present, a negative assertion) is **require-absent** (excluded). When a line's polarity
  cannot be confidently classified the token is dropped — a silent false negative, never the #177
  false positive.
- **Limits (stated so authors don't over-trust it).** Surface keyword presence in the prose is a strong
  signal, not a proof: a token named only via a synonym is a possible false negative, and a generic
  token reused in an unrelated sentence is a possible false negative the other way. When in doubt the
  heuristic stays silent. The `guardrails-review` "stale coverage" probe (issue #157 §2) is the
  human-judgement complement; the breakdown skill keeps the two in sync at authoring time (§157 §3).

---

## 5. Child-process contract

### 5.1 Environment variables (all paths absolute)

| Variable | Set for | Meaning |
|---|---|---|
| `GUARDRAILS_PLAN_DIR` | all | Plan folder root — the **MAIN checkout's** plan dir in ALL modes (the harness's single-writer home for `state/`, `logs/`, the journal); NOT redirected to a segment worktree's checked-out copy even in worktree mode (#134, see the cwd note below) |
| `GUARDRAILS_TASK_ID` | all | Current task id |
| `GUARDRAILS_TASK_DIR` | all | Current task folder |
| `GUARDRAILS_ATTEMPT` | all | 1-based attempt number |
| `GUARDRAILS_STATE_IN` | all | Read-only merged-state **snapshot copy** taken at attempt start; immutable for the attempt |
| `GUARDRAILS_STATE_OUT` | actions | Path the action may write its JSON fragment to (§6.2). Not pre-created; absence after success = "nothing to contribute" |
| `GUARDRAILS_STAGING_DIR` | actions, when `stagingOutputs` declared | Pre-created absolute staging root `<workspace>/.guardrails-staging/<task-id>/`. The action writes its `.claude/`-destined deliverable here under the relative `from` paths; the harness moves staged outputs into their real `.claude/` paths after the action succeeds and before guardrails run (§3.5). Absent for guardrails (verify the real path) and for `--revalidate-task` (no action ran) |
| `GUARDRAILS_STATE_FRAGMENT` | guardrails | Path of the action's (not-yet-merged) fragment, if the action wrote one — lets a guardrail validate proposed state |
| `GUARDRAILS_LOG_DIR` | all | `logs/<runId>/<task>/attempt-N/` — scratch space welcome |
| `GUARDRAILS_WORKSPACE` | actions + guardrails (ALL modes), and re-verify | The effective workspace directory (= cwd). Worktree mode in-attempt: the task's isolated SEGMENT worktree (where the action writes files that `Integrate` commits). Re-verify (§4.3): the INTEGRATION worktree the union bytes were merged into. Serial shared-workspace mode: the plan `workspace`. Set UNIFORMLY across modes so a guardrail/action reading `$GUARDRAILS_WORKSPACE` behaves identically in-attempt, in serial, and at the union point (#124, #130) — e.g. a `stagingOutputs` move lands under this path and a guardrail checking `$GUARDRAILS_WORKSPACE/<to>` finds it regardless of mode |
| `GUARDRAILS_FEEDBACK` | actions, attempt ≥ 2 | Path to `feedback.md` describing the previous attempt's failures |
| `GUARDRAILS_ACTION_STDOUT` | guardrails | The action's captured stdout file |
| `GUARDRAILS_ACTION_STDERR` | guardrails | The action's captured stderr file |
| `GUARDRAILS_ACTION_RESULT` | guardrails | `action-result.json`: `{ "kind", "exitCode", "summary" }` |
| `GUARDRAILS_VERDICT_OUT` | prompt guardrails | Where the verdict JSON must be written (§4.2) |
| `GUARDRAILS_MERGE_BASE` | AI-merge worker | Path to the merge-base copy of the conflicted file on disk (§9.1) |
| `GUARDRAILS_MERGE_OURS` | AI-merge worker | Path to the "ours" copy of the conflicted file on disk (§9.1) |
| `GUARDRAILS_MERGE_THEIRS` | AI-merge worker | Path to the "theirs" copy of the conflicted file on disk (§9.1) |
| `GUARDRAILS_MERGE_OUT` | AI-merge worker | Path the worker writes its resolved merged bytes to (§9.1); the harness reads this file |

**Recorded action outcome — verify, don't replay (issue #62).** `GUARDRAILS_ACTION_RESULT`
/ `_STDOUT` / `_STDERR` hand a guardrail the action's *already-captured* result, so it can
verify a postcondition by inspecting what the action produced instead of re-running the
action's command. Two honesty constraints the guardrail catalogue expands on:
- The action's `exitCode` here is **always 0** — a non-zero action fails the attempt
  *before* any guardrail runs — so a guardrail must never re-assert the exit code (a
  tautology); it verifies recorded *output/artifacts* or upstream state.
- Verify-don't-replay is a speed/flake trade-off, sound only when the postcondition is
  expressible from recorded output the action could not fabricate (a produced artifact, a
  runner-written result file such as a TRX, an upstream state value) — **not** the action's
  own self-reported success line in `_STDOUT`, which is an echo-judge. When the strong
  postcondition isn't expressible from recorded output, re-executing reality is the honest gate.

**Physical write target vs. this table's documented location (issue #266).** For a PROMPT
action/guardrail, the sub-agent is handed a per-attempt STAGING path for `GUARDRAILS_STATE_OUT` /
`GUARDRAILS_VERDICT_OUT` — never the path this table documents — and the harness promotes the
staged file into that documented location immediately after the sub-agent process exits, before
anything else reads it (§9.5). A SCRIPT action/guardrail's target is the documented path directly,
unchanged: only a Claude Code sub-agent's own Write tool call is ever subject to the `.claude/`
sensitive-path block (§9.3), so only the prompt path needs the indirection.

**cwd = `GUARDRAILS_WORKSPACE` (the EFFECTIVE workspace), in every mode** (#134). The action's
and guardrail's process working directory is set to the SAME directory that
`GUARDRAILS_WORKSPACE` names: in worktree mode the task's isolated **segment worktree**; in serial
shared-workspace mode the plan `workspace`; at a union re-verify the integration worktree (§4.3).
This means a file the action writes *relative to its cwd* — not only via `$GUARDRAILS_WORKSPACE` —
lands in the segment worktree that `Integrate` commits, never the user's main checkout.

A `workingDirectory` action override, when set, is resolved **relative to the plan dir** (the
default cwd is the workspace; the override re-bases that cwd onto the plan dir). In **serial**
shared-workspace mode that is the main checkout's plan dir (`GUARDRAILS_PLAN_DIR`, below) —
unchanged. In **worktree** mode the plan folder is physically present *inside* the segment (it is
committed in the repo), so the override is resolved relative to the **segment's copy of the plan
dir** (#135) — otherwise an override-using task's cwd would escape into the user's main checkout, the
same write-escape class as #134. Concretely: the plan dir sits at `<workspace>/<rel>`; in worktree
mode the override resolves under `<segment>/<rel>/<override>`. If the plan dir is *not* under the
workspace (the relative path escapes — the abnormal case; normal plans nest the plan folder inside
the repo), worktree isolation of the override cannot be expressed and the harness **falls back to the
main-checkout plan-dir anchor** rather than fabricate a broken segment path. An override that itself
climbs out of the segment (e.g. `../sibling`) is normalized and resolved, not rejected — containment
is not hard-enforced. This redirect is purely the process **cwd**; `GUARDRAILS_PLAN_DIR` and the
prompt-runner `--add-dir` grant stay the main checkout's plan dir (harness-owned state I/O lives
there, below).

**`GUARDRAILS_PLAN_DIR` and the prompt-runner `--add-dir` grant stay the MAIN checkout's plan dir
in worktree mode** (#134) — they are NOT redirected to the worktree's checked-out copy of the plan
folder. The harness is the single writer of `state/state.json`, the `logs/` tree, and the journal,
all of which live under the main checkout's plan dir; `GUARDRAILS_STATE_IN`/`_OUT`,
`GUARDRAILS_LOG_DIR`, and the fragment the harness reads back are absolute paths under it. The
prompt runner's `--add-dir <GUARDRAILS_PLAN_DIR>` grant must therefore name the main checkout's plan
dir so the agent (whose cwd is the segment worktree) can still reach those absolute state/verdict/log
paths. So in worktree mode the split is: **cwd → segment worktree; harness-owned state/log/plan-dir
paths → main checkout.** The plan folder is also physically present *inside* the segment worktree
(it is committed in the repo), but the agent is pointed at the main-checkout copy for all
harness I/O — the in-worktree copy is incidental and must not be written for state.

Process arguments are passed via `ArgumentList`
(never a concatenated shell string). All child `stdout`/`stderr` is decoded as
UTF-8 and all `stdin` is written as UTF-8 (no BOM), independent of the host console
code page (e.g. the Windows OEM page CP437/850) — so the captured artifacts (§8)
round-trip non-ASCII faithfully and match the harness's own UTF-8-no-BOM writes
(`AtomicFile`). For prompt processes, the same information is *embedded in the
composed prompt* (agents read instructions, not env vars).

**On Windows, a script launched THROUGH BASH sees `GUARDRAILS_*` path values in forward-slash form**
(issue #263) — `C:/Users/...`, a straight backslash→forward-slash swap of the same absolute path, not
the MSYS `/c/Users/...` mount form. .NET absolute paths on Windows are backslash-separated; bash's own
path handling (`cd`, `test -f`, `[ -f ... ]`) tolerates that fine, but a guardrail that interpolates
the SAME value into an escape-sensitive context another language/tool parses — a `node -e` JS string
literal, a regex, `sed`, `awk`, `perl -e` — has each backslash silently consumed as an escape
character, corrupting the path (`\2` read as an escape) and failing with a misleading downstream error
that looks like a domain bug in the guardrail rather than harness path corruption. The conversion is
scoped tightly: **Windows only** (a no-op everywhere else — paths are already forward-slash native),
**bash-resolved interpreter only** (gated on the §5.2 interpreter map's resolved executable, not merely
the `.sh` extension, so a config-overridden `.sh` interpreter that is NOT bash is unaffected — a
PowerShell `.ps1` script keeps its native backslash form, since PowerShell's own path handling is
backslash-native), and **`GUARDRAILS_`-prefixed keys only** — a task's own declared `action.env`
entries (§2) are never touched, so an author's literal value is never second-guessed.

### 5.2 Interpreter map (built-in defaults)

| Extension | Command line (first available wins) |
|---|---|
| `.ps1` | `pwsh -NoProfile -ExecutionPolicy Bypass -File {script}` → fallback `powershell.exe …` (Windows only) |
| `.sh` | `bash {script}` |
| `.py` | `python3 {script}` → fallback `python {script}` |
| `.cmd` / `.bat` | `cmd /c {script}` (Windows only; validation error elsewhere) |
| `.dll` | `dotnet {script}` |
| none / `.exe` | direct spawn |

`guardrails.json: interpreters` extends/overrides these. `{script}` and `{args}` are
substitution tokens (`{args}` defaults to appending after the script path).
`guardrails validate` reports any extension used by the plan whose interpreter is
not resolvable on PATH.

### 5.3 Harness writes to the workspace — three bounded cases

**The harness writes only the harness-owned integration worktree (plan branch
`guardrails/<plan-name>`), via integration, after a task's action and guardrails succeed in its
segment worktree — and never otherwise. The user's checkout is read-only for the entire run.**

There are two kinds of integration. **(A) Fast-forward** (a linear chain's commit, no sibling has
advanced the plan branch): `git merge --ff-only` — **no new union, no re-verify** (the bytes already
passed the task's guardrails in the segment worktree). **(B) Union** (a fan-in, or a non-FF
integration where a sibling raced): a real merge that MUST be re-verified on the merged bytes before
the commit.

**Union resolution: git auto-merge → AI-merge → human.** `git merge --no-commit`; on conflict, the
**AI-merge worker** (a constrained prompt behind `IPromptRunner`, §9.1) produces merged BYTES only,
trusted via two **deterministic** checks — (i) no conflict markers remain (`git diff --check`),
(ii) blast-radius: it modified only the git-reported-conflicted files (`git status --porcelain`); an
out-of-bounds write or a remaining marker ⇒ discard (`reset --hard`) + needs-human. 1 retry. The AI
resolves harness-internal unions only; it is **withheld** at the `--merge-on-success` user-branch
boundary.

**The verdict (identical for clean-auto and AI-resolved) is the deterministic re-verify:** re-run
the run's **integration-guardrail set** (§4.3) on the `--no-commit`
merged bytes, then assert `git status --porcelain` shows only the staged merge (W3 read-only check).
Any re-verify fail / remaining conflict / dirtied tracked file ⇒ `git reset --hard preHead`;
`needs-human`; write no fragment, consume no `mergeSequence`. AI-merge + its re-verify run in the
fan-in's **private forked worktree OFF the serialize lock**; only the integration of the verified
result into the plan branch is **under the lock**, with a staleness re-verify against the current
plan-branch bytes.

**The atomic settle (state + git + journal as one ordered unit, under the serialize lock).** On
success, in this FIXED order: (1) deep-merge the task's fragment into `state.json`; (2) `git commit`
the integration (the FF move for case A, the merge commit for case B) carrying the parseable
`Guardrails-Task: <taskId>` / `Guardrails-Run: <runId>` / `Guardrails-Task-Hash: <definitionHash>`
trailer — **written on the plain FF'd commit as well as on merge commits**, so resume can read FF
integrations (§7) AND detect whether the task's definition changed since that commit (the
definition-drift halt, §7.2). The `Guardrails-Task-Hash` line is **omitted when the hash is
unavailable** (old commits, fake providers) — backward-compatible; (3) consume the
`mergeSequence` + journal `Succeeded`. The fragment merge precedes the commit so the resume pre-pass
can never treat a task succeeded-by-commit while its state is missing. Every non-success path is a
single `git reset --hard preHead` (NOT `merge --abort`, which fails rc=128 on the dirtied-tracked
path) — leaving state, git, and journal all UNCHANGED, never half-merged, and the user's checkout
untouched.

**Internal commits bypass user git hooks (#149).** Every commit the harness makes for its own
bookkeeping — the segment integration commit (`git commit --no-verify --allow-empty …` in `Integrate`)
and the non-FF union merge commit (`git commit --no-verify …` in `CommitStagedMerge`) — runs with
`--no-verify`. These are machine commits in throwaway worktrees on the `guardrails/<plan>` branch, not
the user's deliverable; a global user `pre-commit` hook (e.g. GitGuardian's `ggshield`) must never gate
them. The incident that motivated this: an offline `ggshield` `pre-commit` hook failed the internal
state-marker commit and crashed the run. User hooks run only on the **user-facing** merge-back (below).

**A git/IO failure during integration is a `needs-human` halt** routed through the normal failed path,
never an uncaught throw. More broadly, **any unexpected infrastructure fault during a run** (a task
executor or an integration step throwing — git unavailable, a failing internal hook that somehow still
fired, an IO error) is an **honest halt, not an unhandled crash (#150)**: the scheduler terminates the
worker pool, runs the end-of-run cleanup sweep, and returns an **aborted `RunReport`** carrying a
`RunAbort` (one-line `Headline` + `Remedy` for the console, full exception `Detail` for the logs). The
CLI renders the one-liner + remedy, writes the full fault to `logs/<runId>/abort.log`, and exits
non-zero (harness error) — never a raw stack trace as the headline. An aborted report is failed
regardless of per-task outcomes.

**Retry preserves upstream work:** a failed attempt is `git reset --hard <taskBase> + git clean -fd`
in its segment worktree (keeping every upstream/sibling commit; `taskBase ≠ preHead`), not a
discard-and-recreate.

**Run end (opt-in delivery).** When the run drains wholly green AND `mergeOnSuccess`/
`--merge-on-success` is set, the harness merges the plan branch into the user's original branch
(ff-only when possible, else a real merge whose re-verify must pass). **AI-merge is NOT used here.**
A conflict / failed re-verify / dirty user tree halts to `needs-human`, plan branch intact — never a
force-overwrite. Default OFF leaves the plan branch for the user to review and merge. The merge-back
outcome is reported as `MergeOnSuccessResult` (`FastForwarded` / `Merged` / `Conflict` /
`DirtyWorkingTree` / `HookRejected`); a dirty user working tree is refused **before any git merge runs**
(the harness never runs git over uncommitted user work) and returns `DirtyWorkingTree`.

**The user-facing merge KEEPS the user's git hooks (#149).** This is the deliberate complement to the
internal-commit isolation above: when the verified plan branch lands on the user's real branch, their
`pre-commit`/`commit-msg` hooks (GitGuardian, lint, …) SHOULD run, exactly like a manual `git merge`.
The non-FF merge commit (`git commit --no-edit`, no `--no-verify`) therefore runs them.
- **`HookRejected`**: a hook rejected that merge commit (e.g. a secret found, or — as in the incident —
  the hook ran offline and failed). The harness runs `git merge --abort` (best-effort) so the user's
  branch is left **clean at its original HEAD**, leaves the plan branch intact, and returns
  `HookRejected` carrying the hook's **stderr** (threaded out via `RunReport.MergeOnSuccessDetail`) so
  the CLI shows the actual reason + a remedy ("resolve and merge manually, or disable the hook for the
  merge"). The tasks all passed and are durable on the plan branch — a graceful halt, not a failure.
- **Inherent FF caveat (intended):** the fast-forward delivery path creates **no commit**, so no commit
  hook fires there — identical to a manual `git merge --ff-only`. Hooks run only on the non-FF merge
  commit. A user who needs the hook to vet every delivery should expect it only when the merge-back is
  non-FF (their branch advanced during the run).

A wholly-green run whose delivery is HALTED (`Conflict` / `DirtyWorkingTree` / `HookRejected`) exits
non-zero at the CLI: the work is durable on the plan branch but the user must act. A `FastForwarded` /
`Merged` delivery, or no `mergeOnSuccess` at all, leaves the green (exit 0) verdict untouched.

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

Any new capability that needs the harness to write outside the integration worktree or the opt-in
end-of-run delivery to the user's branch must be added to this section with its own containment
analysis — the default remains that the harness does not mutate the user's checkout.

---

## 6. State

### 6.1 Lifecycle

- `state/seed.json` (optional, **committed**): initial state authored with the plan.
- `state/state.json` (runtime, gitignored): the merged state. Created at run start
  from `seed.json` (or `{}`) when missing. `guardrails run --fresh` deletes runtime
  state and re-seeds. The `--fresh` deletion list is: `run.json`, `state.json`,
  `merge-conflicts.log`, `state/captured/`, and the plan-root `logs/` tree (all runs'
  attempt artifacts and any static log site, on-the-fly or exported, §8/§12.3). It **also tears down
  the plan branch `guardrails/<plan-name>` and its worktrees** (issue #274, part B): the plan branch is
  the durable cross-run resume record — its `Guardrails-Task:` trailers drive the "already succeeded,
  skip it" pre-pass (§7) — so, unlike the stale segment/fork branch prune which deliberately *preserves*
  it, a genuine fresh slate must remove it (branch + its integration worktree + any orphaned
  `_integration` directory under the plan's worktree root), else a "fresh" run silently reuses the stale
  trailers and re-skips edited tasks. This teardown fires **only** on the explicit `--fresh` /
  `guardrails reset` (full-reset) path — a normal resume preserves the plan branch and resumes against
  it. It does **NOT** delete `state/guardrails-review.json` — that marker is a committed plan artifact
  (§13), planHash-keyed so it self-invalidates on any edit, NOT per-run runtime state.
- The **harness is the single writer** of `state.json`. Child processes never touch it.

### 6.2 Fragments (snapshot in, fragment out)

Each attempt receives an immutable snapshot (`GUARDRAILS_STATE_IN`). An action that
wants to publish state writes a JSON **object** to `GUARDRAILS_STATE_OUT`, with every
top-level key namespaced under its own task id —

```json
{ "02-generate-greeting": { "greetingPath": "out/greeting.txt" } }
```

**Single-writer-per-key (ENFORCED).** A merged fragment's top-level keys must each be the
writing task's **own id** (or a harness reserved key — **none in v1**, see
`ReservedMergeKeys` below). A fragment with **any other** top-level key — a **foreign task
id** OR an arbitrary **shared** (non-task) key — fails as **invalid-fragment** and is **NOT**
merged (the attempt fails, retries with feedback naming the stray key, and nothing reaches
`state.json`). The fragment is **rejected, not stripped**. This makes the harness the single
writer of every task's namespace, closing the #48 cross-task poisoning vector: no task can
overwrite another task's captured `fileHashes` (or any derived key) by writing under that
task's id. `needsHuman` is **exempt** — it short-circuits the attempt (§9) *before* the merge
step, so it is never subject to this rule.

A fragment that exists but is not a parseable JSON object ⇒ the attempt **fails**
(reason: "invalid state fragment") and is retried — better than silently dropping data.
An **empty** object `{}` passes vacuously (no keys) and merges nothing. The fragment is
merged only after **all guardrails pass**.

**`ReservedMergeKeys`** is the harness allowlist of top-level keys permitted in addition to
the writing task's own id. It ships **EMPTY** in v1 — there is deliberately no shared writable
namespace. Any future reserved key MUST carry its own anti-poisoning analysis before admission:
a shared writable key is exactly the cross-task poisoning vector this rule closes.

**Cross-task state references require a dependency edge (validated, GR2022).** A guardrail or
script-action body that reads another task's state namespace in the canonical state-access form —
`$state.'<task-id>'` / `$state."<task-id>"` (PowerShell) or `state['<task-id>']` /
`state["<task-id>"]` (bracket index) — declares a *runtime read dependency* on that producer. The
scheduler orders only on `dependsOn`, so if the producer is not a transitive `dependsOn` ancestor of
the consumer, the scheduler may run the consumer first and the read returns null — the guardrail then
fails at runtime as `needs-human` for a reason that was knowable at load time (the `46`→`35` cascade,
issue #121). `guardrails validate` therefore turns this into a load-time **ERROR (GR2022)**: for every
referenced `<task-id>` that is a real task id in the plan and is **not** the referencing task's own id,
that task MUST be reachable as a transitive `dependsOn` ancestor — **OR** be satisfied by the
pre-existing baseline, i.e. `state/seed.json` carries a top-level key exactly equal to `<task-id>`
(§6.1/§6.3 establish seed content as a legitimate non-ancestor source under a task's namespace). The
check is deliberately scoped to the canonical state-key *shape* — the exact form the single-writer-per-key
namespacing makes deterministic (the producer of key `'<id>'` is exactly task `<id>`, never ambiguous) —
so it carries **zero false-positive risk**: an id that matches no task, or a quoted string not in a
`state` access position, is ignored. **Produced-file references** (a guardrail reading a path another
task's action writes) are *not* linted in v1 — no deterministic producer→artifact map exists
(`writeScope` is an optional, glob-shaped permission surface, not a write manifest), so a file-level
check could not meet the zero-false-positive bar; it is a future tightening, gated on such a map existing.

### 6.3 Merge policy (deterministic)

Deep merge into `state.json`: objects merge recursively; **scalars and arrays are
last-writer-wins**. Merge order = task completion order, recorded as a monotonic
`mergeSequence` in the journal. Every overwrite of an existing non-null value with a
*different* value is appended to `state/merge-conflicts.log` — tab-separated columns
`seq, task, jsonPath, old, new`, with values as compact JSON.

With single-writer-per-key enforced (§6.2), last-writer-wins is reachable only **WITHIN a
task's own namespace** (a task overwriting a value it previously wrote under its own id) or
against committed **`seed.json`** content under that namespace — **never cross-task at the
root**. A conflict row's `jsonPath` therefore always begins with the writing task's own id
(e.g. `01-author.fileHashes."Tests.cs"`).

---

## 7. `state/run.json` (journal)

```jsonc
{
  "version": 1,
  "runId": "2026-06-10T16-22-31Z-a1b2",
  "planHash": "sha256:…",          // hash of guardrails.json + all task.json; mismatch on resume ⇒ loud warning
  "nextMergeSequence": 3,
  "tasks": {
    "01-write-greeting-script": {
      "status": "succeeded",        // pending | running | succeeded | needs-human | blocked | failed
      "mergeSequence": 1,
      "definitionHash": "sha256:…", // task.json + action.* + guardrails/** + preflights/**, stamped at
                                    // this task's most recent successful settle. Absent on a journal
                                    // entry predating this field (treated as "unknown — assume
                                    // unchanged," never forces a halt on upgrade). See §7.2.
      "attempts": [
        {
          "attempt": 1,
          "startedAt": "…", "endedAt": "…",
          "actionExitCode": 0,
          "outcome": "succeeded",   // succeeded | action-failed | guardrail-failed | timeout | output-cap | rate-limited | cancelled | invalid-fragment | needs-human | permission-denied | task-preflight-failed
          "failedGuardrails": [ { "name": "02-tests-exist", "reason": "no *.Tests.csproj found" } ],
          "costUsd": null,          // prompt attempts: total_cost_usd from the runner
          "logDir": "logs/2026-06-10T16-22-31Z-a1b2/01-write-greeting-script/attempt-1",
          // OPTIONAL per-attempt provenance the harness knew at launch (#198). Additive — a script /
          // serial attempt or an older journal OMITS fields (or the whole section); never null noise.
          // Also mirrored to <attempt>/attempt-provenance.json (§8).
          "provenance": {
            "model": "claude-…",    // FULLY RESOLVED --model (#200): task.json action.model if set, else
                                     //   promptRunners.<name>.model, else "(cli default)"; ABSENT for a script task
            "segmentBranch": "guardrails/2026-…-a1b2/01-write-greeting-script/attempt-1",
            "worktreePath": "/…/guardrails-worktrees/…",
            "baseCommit": "sha…"    // the commit the segment forked from (taskBase); ABSENT in serial mode
          }
        }
      ]
    }
  },

  // OPTIONAL top-level sections — two-scope preflights (F9 split). Additive: a plan WITHOUT the
  // feature OMITS both (an older reader ignores them; absent, never null noise). Each is planHash-keyed.
  "planPreflights": {                   // the PRE-DAG preflight phase result (OUTSIDE tasks{})
    "status": "plan-preflight-failed",  // passed | plan-preflight-failed
    "planHash": "sha256:…",
    "evaluatedAt": "2026-06-10T16-22-30Z",
    "checks": [ { "name": "git-top-level", "passed": false, "reason": "workspace is not a git top-level" } ]
  },
  "planGuardrails": {                    // the TERMINAL <plan>/guardrails/ gate on the merged HEAD (OUTSIDE tasks{})
    "status": "plan-guardrail-failed",  // passed | plan-guardrail-failed
    "planHash": "sha256:…",
    "failedChecks": [ { "name": "whole-repo-build", "reason": "CS0111 duplicate member from a merge collision" } ],
    // OPTIONAL #175/#205 merge-collision advisory — present only on failure when ≥2 tasks have
    // OVERLAPPING writeScope on a shared file; names the offending task pair(s) + shared path(s). ABSENT
    // (never null noise) when the gate passed or no two writeScopes overlap.
    "collisionHint": "This may be a merge collision: … '07-…' & '09-…' (shared: Launcher.cs)"
  }
}
```

**Attempt outcomes** (the per-attempt `outcome` field; distinct from task `status`):
- `action-failed` — a generic non-zero action / `is_error` with no recognized signal.
- `timeout` — the action (or a guardrail) exceeded its timeout (issue #119). The retry carries
  timeout-specific feedback ("don't re-explore; go straight at the deliverable") AND a **longer clock**
  (1× → 1.5× → 2.25× …, capped 4×) — a same-clock retry just re-times-out. The feedback is **mode-aware**
  (issue #167): in serial mode it says "continue from the preserved partial work"; in worktree mode, where
  a non-final attempt's segment is reset to `taskBase` + cleaned before the next attempt, it instead
  discloses the file-write rollback and instructs re-authoring (the same disclosure as the state-rejection
  path, §6.2) — never the false "your partial work is preserved on disk" claim.
- `output-cap` — a prompt action's response exceeded the runner's output-token cap (issue #114). A
  budget-exhaustion failure distinct from `action-failed` so a human (and §9 triage) sees the agent
  ran out of OUTPUT budget; the retry carries "write incrementally / split" feedback. **Retry salvage
  (issue #195, §3.2):** in worktree mode, when `preserveAttemptsForSalvage` is on (default), a non-final
  attempt's full working tree is preserved to `refs/guardrails/<taskId>/attempt-<N>` immediately before
  the F2 reset discards it, and the feedback names the ref + a `git diff --stat` summary.
- `max-turns` — a prompt action exhausted its TURN budget mid-progress (issue #129 / #94; Claude
  `error_max_turns`). A budget-exhaustion failure distinct from `action-failed` so a human (and §9
  triage) sees the agent ran out of TURNS — not a logic failure. The retry carries "work directly toward
  the deliverable" feedback AND a **raised turn budget** (1× → 1.5× → 2.25× …, capped 4×, rounded up) —
  a same-budget retry just re-exhausts at the same cap. Like the timeout feedback, this is **mode-aware**
  (issue #167): serial mode says "continue from the preserved partial work"; worktree mode discloses the
  segment reset / file-write rollback and instructs re-authoring (the raised-turn-budget advice survives
  in both modes). **Retry salvage (issue #195, §3.2):** identically to `output-cap` above, a worktree-mode
  non-final rollback is preserved to a salvage ref by default — this is WHY the worktree-mode feedback's
  "your prior writes are gone" disclosure is softened to "reverted from your working tree, but not
  discarded — see the ref" whenever a salvage ref was actually created. `timeout` does NOT get this
  treatment (out of scope for #195 — see §3.2's scope guard); its rollback disclosure is unchanged.
- `rate-limited` — a transient infrastructure limit did not clear within
  `transientPauseBudgetSeconds` (issue #115). The harness paused+re-ran WITHOUT consuming the retry
  budget; only on budget exhaustion did it settle `needs-human` with this outcome ("re-run later"). A
  transient pause that DOES clear is never journaled (observe-only via the `PromptPaused` event).
- `permission-denied` — the runner refused a write/edit because the path is not on the granted
  permission allow-list, and the wall is un-retryable (issues #86 / #104, §9.3). The harness settled
  `needs-human` EARLY — on the FIRST hit for a structural `.claude/` path (the Claude Code sub-agent
  runtime blocks automated `.claude/` writes even under `acceptEdits`), or on the REPEAT for any other
  path refused across two or more attempts — instead of burning the remaining retry budget on the
  identical wall. The attempt carries this DISTINCT outcome so a human (and §9.2 triage) sees a
  permission/config issue, not a generic `action-failed`.
- `task-preflight-failed` — a per-task `tasks/<id>/preflights/` slot failed (the two-scope preflights F9
  split). The task-scoped preflight gate did not pass, so the harness settles the task `needs-human` and
  its transitive cone `blocked` (exit 2) WITHOUT running the action. A per-attempt `outcome` inside
  `tasks{}`, distinct from `action-failed`/`guardrail-failed` so a human (and §9 triage) sees a preflight
  gate failure — not a generic action failure. Recorded as a real attempt record (`attempt: 1`) carrying
  this `outcome` plus the failed preflight check(s) in `failedGuardrails` (`{ "name", "reason" }`), so
  `run.json` shows WHAT gate failed and WHY. **No-burn is STRUCTURAL, not signalled by attempt-list
  emptiness:** the short-circuit records exactly ONE attempt and fires BEFORE the attempt loop and before
  the task is marked `running`, so the retry budget is never consumed (a burned retry would produce a
  second attempt) and no transient `running` status is ever written. Distinct from the two whole-plan
  phase halts (`plan-preflight-failed`/`plan-guardrail-failed`), which live OUTSIDE `tasks{}` in the
  top-level sections below.

**A succeeded task records a real attempt in BOTH modes (#196).** A task that settles `succeeded` journals
a `succeeded` attempt record in `attempts[]` — in **serial** mode inline as the attempt completes, and in
**worktree** mode at the deferred B1 settle (the executor computes the attempt data and threads it to the
scheduler, which records it TOGETHER with the reserved `mergeSequence` under the integration lock). Both
paths write the identical attempt shape, so a succeeded task's `attempts[]` is non-empty regardless of
mode. Each attempt also carries the OPTIONAL `provenance` block (#198) — the model + segment worktree +
base commit the harness knew at launch (see the wire example above); it is mirrored to
`<attempt>/attempt-provenance.json` (§8).

**Top-level plan-phase sections (two-scope preflights, F9 split)**

Two OPTIONAL top-level journal keys record the two whole-plan phases that run OUTSIDE `tasks{}`. Both are
**additive and backward-compatible**: a plan WITHOUT the feature **omits** them (an older reader ignores
them; they are absent, never `null` noise), and the existing `tasks{}` shape is untouched. Each is
**`planHash`-keyed** — it records the plan hash it evaluated against.
- `planPreflights` = `{ "status", "planHash", "evaluatedAt", "checks": [...] }` — the **pre-DAG** preflight
  phase result. `status` is `passed` or **`plan-preflight-failed`** (the pre-DAG phase failed → halt BEFORE
  scheduling any task → exit 2). `checks[]` are the individual preflight results
  (`{ "name", "passed", "reason"? }`).
- `planGuardrails` = `{ "status", "planHash", "failedChecks": [...] }` — the **terminal**
  `<plan>/guardrails/` gate evaluated on the merged plan-branch HEAD. `status` is `passed` or
  **`plan-guardrail-failed`** (the terminal gate failed → exit 2). `failedChecks[]` are the failed
  guardrails (`{ "name", "reason" }`, the same shape as a task attempt's `failedGuardrails`).

**Pre-DAG resume SKIP rule (the B1 fix).** The pre-DAG `planPreflights` phase runs BEFORE the Scheduler
builds any wave, evaluating `<plan>/preflights/` against the run's STARTING bytes (the integration
worktree on the plan branch at the user's HEAD in worktree mode; the plan workspace directly in serial
mode) — once, via the unconditional `IReVerifier` seam (§4.3). On a plain resume (no `--fresh`), the
harness reads the existing `planPreflights` marker FIRST: when `status == "passed"` AND its `planHash`
matches the CURRENT plan hash, the phase is **SKIPPED** — the marker (`evaluatedAt` and `planHash`) is
left byte-for-byte untouched, and scheduling proceeds straight to the DAG. The phase re-evaluates (and
overwrites the marker) only when the marker is absent, its `status` is `plan-preflight-failed`, its
`planHash` is stale (the plan changed since the marker was written), or `--fresh` deleted `run.json` (§6.1)
before this phase runs. This is load-bearing, not an optimization: many plan-level preflights are
**negative-baseline** checks — true only at the very start of a plan's lifecycle (e.g. "artifact X does
not yet exist"), because a task later in the DAG legitimately introduces the condition the check forbids.
Re-running the check on every resume would evaluate it against **partially-merged mid-DAG bytes** and
false-halt a run that is actually fine; evaluating it exactly ONCE per `planHash` — at the true start,
before any task has touched the workspace — is the only reading that makes the check meaningful.

**Status semantics**
- `succeeded` — terminal. Resume skips it; `guardrails reset <folder> <task>` is the
  explicit way to force a re-run.
- `needs-human` — retry budget exhausted, OR (issue #115) a transient limit that did not clear within
  the pause budget (a `rate-limited` attempt — re-run later), OR (issue #174) a **no-op deadlock**
  short-circuit (below). All *transitive* dependents become `blocked`. Independent branches keep running.

**No-op-deadlock short-circuit (issues #174 / #182).** After a guardrail-failed attempt, the harness
settles `needs-human` IMMEDIATELY — instead of exhausting the remaining retry budget — when **both**
hold: (a) the action made **no observable change** this attempt (a *genuine no-op*), AND (b) the
guardrail failure is **byte-identical** to the previous attempt's, which was **also** a no-op. A no-op
action cannot fix a guardrail failure it did not cause (e.g. the terminal `<plan>/guardrails/` gate
re-verify against a merge artifact, §3.3 / issue #175), and an unchanged failure proves nothing converged — so a
further attempt has zero probability of differing. This fires on the **2nd** such attempt (the earliest
point both conditions can be observed).

"No observable change" is established per mode, because the two modes have different evidence available:
- **Worktree mode (#174):** the action exited 0, wrote no state fragment, AND touched no file versus
  `taskBase` (proven by the segment-vs-`taskBase` git diff).
- **Serial mode (#182):** there is no `taskBase` to diff files against, so the file-diff half is
  unavailable. The serial signal substitutes a **byte-identical action-output** requirement: the action
  exited 0, wrote no state fragment, AND its **stdout/stderr is byte-identical** across the two attempts
  (the proxy for "the action behaved identically this attempt"). Combined with the byte-identical
  guardrail failure, this is the conservative evidence that a further attempt cannot differ — even if the
  action silently wrote a workspace file, an unchanged guardrail output across two such attempts proves
  that write (if any) is irrelevant to convergence. The serial path **never** loosens the
  byte-identical-guardrail-failure requirement that is the core "cannot converge" evidence.

It is **conservative**: it never fires when the action wrote a state fragment (the action DID work, so
retrying may help), never in worktree mode when the segment diff reports file changes, never in serial
mode when the action's stdout/stderr CHANGED between attempts (a task slowly converging via changing
output keeps its full budget), and never (in either mode) when the guardrail output CHANGED between
attempts (those can still converge). The short-circuit settles the task `needs-human` via the same
status transition as budget exhaustion; only a non-final attempt takes this path (the final attempt
already exhausts to `needs-human`).
- Resume rules (`guardrails run` on an existing journal): `succeeded` → skip;
  `needs-human` / `failed` / `blocked` → `pending` with a fresh retry budget;
  `running` (crashed previous run) → `pending`, attempt numbering continues.

**Resume does not distinguish WHY a task is `needs-human` (issue #190, documented — not tightened).**
On a plain `guardrails run` resume (not `--fresh`), **ANY** task journaled `needs-human` — for **any**
reason, including a genuine unresolved human-decision halt — is reset to `pending` and given a
**FRESH retry budget**. `RunJournal.ResumeStatus` is a pure function of the journal `status` string
alone; it does not inspect the task's last recorded `AttemptRecord.Outcome` to tell "this will
probably self-resolve" (`rate-limited`, and likewise a transient `timeout`/`output-cap`/`max-turns`
exhaustion) apart from "a human must actually act first" (the `needsHuman` prompt short-circuit, a
`permission-denied` wall, a `task-preflight-failed` gate, or a genuinely exhausted guardrail-failure
retry budget). Re-running the plan without having fixed anything therefore silently burns a full fresh
retry budget re-attempting the SAME thing that already exhausted its budget — likely to fail
identically (partially mitigated by the no-op-deadlock short-circuit above, which still needs 2
identical no-op attempts to fire, not an immediate park).
<br>
This was evaluated for a clean tightening (teaching `ResumeStatus`/`ApplyResumeRules` to look at the
last `AttemptRecord.Outcome` and auto-reset to `pending` ONLY for the auto-retryable infra outcomes —
`rate-limited`/`timeout`/`output-cap`/`max-turns` — leaving a genuine `needsHuman`/`permission-denied`/
`task-preflight-failed`/exhausted-guardrail-failure outcome parked at `needs-human` until an explicit
`guardrails reset <folder> <task>`) and was **deliberately NOT implemented**: the existing resume-matrix
test (`RunJournalTests.Resume_NormalizesStatusPerSsot`) already locks in "ANY non-succeeded status →
`pending`" as outcome-agnostic SSOT-tested behavior, and the change would ripple into the Scheduler's
resume pre-pass and require auditing every current and future `AttemptOutcome` for "does this still
auto-retry on resume?" — real surface area for a behavior change that is not this issue's stated
minimum bar. If a future issue wants the tightening, start from this note.

**Hand-fixing a `needs-human` when the fix is a merged WORKSPACE file (issue #197).** The normal
guidance — "inspect the latest attempt's `feedback.md`, fix the action or guardrails, then re-run to
resume" — assumes the fix lands in a PLAN-FOLDER file (`task.json`, an action script, a guardrail),
which a human edits directly in the plan dir on the run branch. In **worktree mode**, a fix
sometimes needs to land in a **workspace SOURCE file an upstream task already wrote and merged onto
the harness's internal integration branch** — and the user's own checkout is **read-only for the
entire run** (Load-bearing invariant: worktree isolation), so editing it there does nothing; the fix
must be committed on the harness's integration branch itself. Steps (verified against the actual
`GitWorktreeProvider` implementation, not guessed from naming conventions):

1. **Find the plan branch.** `git branch -a | grep guardrails/` or `git worktree list` — the plan
   branch is named `guardrails/<plan-name>` (the plan FOLDER's name, e.g. `guardrails/hello-guardrails`
   — NOT a hash; `git worktree list` also shows every live worktree's path and the branch it has
   checked out).
2. **Identify the integration worktree.** It is the worktree checked out on the plan branch, at
   `<worktreeRoot>/<runId>/_integration` under the harness-owned worktree root (default
   `<temp>/guardrails-worktrees/<plan-folder-name>-<hash>/`, §1 — overridable via `guardrails.json`'s
   `worktreeRoot`). `git worktree list` output makes this unambiguous: the path ending `.../_integration`
   is it.
3. **Edit + commit the merged file THERE** — `git -C <integration-worktree-path> add <file>` then
   `git -C <integration-worktree-path> commit`, or `cd` into that worktree and use plain `git`. This is
   an ordinary human commit (NOT one of the harness's own internal `--no-verify` plumbing commits,
   §5.3 "Hook policy") — it runs YOUR local `pre-commit`/`commit-msg` hooks normally, which is fine and
   expected; it is just worth knowing your fix-commit behaves differently from the harness's own
   internal commits (which deliberately bypass hooks) so the difference isn't confusing.
4. **Re-run to resume.** `GitWorktreeProvider.CreateSegment` forks every new segment worktree off a
   **live `git rev-parse` of the plan branch's current tip** at the moment it is created — never a
   cached/stale reference — so the human's commit, once on the integration branch (which IS the plan
   branch's worktree), becomes the base the next attempt for that task inherits. No extra step is
   needed to "publish" the fix; the next resumed attempt sees it automatically.

### 7.1 Re-validate-only (`guardrails run --revalidate-task <id>`)

`guardrails run [folder] --revalidate-task <task-id>` runs **only that one task's guardrails**
against the **current workspace state**, spawning **no action/agent attempt** (issue #102). The use
case: a task hit `needs-human`, a human hand-fixed the artifact in their checkout, and they want to
confirm the gate now passes WITHOUT burning another agent attempt that might redo expensive work or
overwrite the fix. It is a single-task verification, **not** a run — the rest of the DAG is untouched
(a subsequent normal `run` resumes it).

- **Workspace / cwd.** Guardrails run with cwd = the plan `workspace` (the user's own checkout, where
  the fix lives) — the same serial/shared-workspace path a `maxParallelism: 1` run uses.
- **Worktree mode is refused.** When a normal run would use worktree isolation (`maxParallelism > 1`
  on a git workspace), `--revalidate-task` exits `1` with a pointer to set `maxParallelism: 1`: an
  in-place fix in the user's checkout is invisible to a fresh isolated segment worktree, so verifying
  it there would be meaningless.
- **No action output, no fragment.** The `GUARDRAILS_ACTION_STDOUT` / `_STDERR` / `_RESULT` pointers
  (§5.1) are **absent** — no action ran — so a verify-don't-replay guardrail (#62) that requires them
  fails honestly rather than passing vacuously. `GUARDRAILS_STATE_IN` is a fresh snapshot of the
  current `state.json`; **no fragment is produced or merged** (the human's artifact is the deliverable,
  not new state — any state earlier attempts contributed is already in `state.json`). Prompt guardrails
  run via the same path as a normal attempt; they are NEVER silently skipped.
- **Eligibility.** Refused (exit `1`) for an unknown task id, an already-`succeeded` task (use
  `guardrails reset <id>`), or a task with a dependency that is not yet `succeeded` (the DAG invariant:
  a task only goes green after its deps). Eligibility is read from the **durable** journal status
  (before resume normalization). Cannot be combined with `--fresh` or `--dry-run`.
- **Settle.** All guardrails pass ⇒ a synthetic `succeeded` attempt is journaled and the task settles
  `succeeded` (`state.json` unchanged); exit `0`. Any guardrail fails ⇒ a `feedback.md` is written, the
  failing guardrails are reported, the task settles `needs-human` (still a non-green halt the human must
  keep working); exit `2`. No agent is spawned in either case.

**Reserved synthetic ids — `plan:guardrails` / `plan:preflights` (deliverable 4).** Two reserved
task-id-shaped strings are accepted by the SAME `--revalidate-task <id>` flag above (no new verb, no new
C# symbol on the CLI surface) to re-validate a WHOLE-PLAN phase instead of a task. The `:` character is
already disallowed in a real task id (§3 `^[a-z0-9][a-z0-9._-]*$`), so neither can ever collide with an
authored task.
- **`--revalidate-task plan:guardrails`** re-runs ONLY the terminal `<plan>/guardrails/` checks (§3.3)
  against the CURRENT merged HEAD. UNLIKE a per-task revalidate, **worktree mode IS supported**: the
  gate's subject is the merged HEAD itself (the integration worktree the harness owns), never an
  in-place fix in the user's own checkout, so the worktree-mode refusal above does not apply here. All
  checks pass ⇒ `planGuardrails` is journaled `passed`, exit `0`. Any check still fails ⇒ journaled
  `plan-guardrail-failed`, exit `2` — the same terminal-halt outcome an ordinary `run` would have
  produced. A plan with no `<plan>/guardrails/` folder has nothing to revalidate: exit `1`.
- **`--revalidate-task plan:preflights`** is the symmetric analogue for the pre-DAG
  `<plan>/preflights/` phase (§7 above) — re-confirming a hand-fixed starting state without burning an
  agent attempt. Journals `planPreflights`; same pass/fail/no-folder exit-code shape.

**Terminal-only resume (B2(b)).** After a terminal halt (`planGuardrails.status ==
"plan-guardrail-failed"`), a plain `guardrails run` (no `--revalidate-task`) is an ordinary resume:
every already-`succeeded` task SKIPS via the existing resume rule above (no attempt burned), the DAG
drains with nothing left to run, and — because the terminal phase carries no passed-marker skip (unlike
the pre-DAG phase's B1 rule: the terminal phase always evaluates the CURRENT merged HEAD, so there is no
negative-baseline concern to guard against) — it unconditionally re-fires `<plan>/guardrails/` against
that same HEAD. Still red ⇒ `plan-guardrail-failed` again, exit `2`; hand-fixed to green ⇒ `passed`,
exit `0`. A `planHash` mismatch (the plan changed since the failed marker was written) needs no special
case: it simply falls through to this same normal resume.

**Harness exit codes**: `0` all succeeded · `1` harness/validation error — including a run **aborted**
by an unexpected infrastructure fault (#150: an honest halt rendered from the aborted `RunReport`, full
fault in `logs/<runId>/abort.log`, never a raw stack trace) · `2` the operation completed but an
actionable condition was found — for `run`: a task is needs-human/blocked, OR the pre-DAG
`planPreflights` phase failed (§7, exit **before** any task is scheduled), OR the terminal `planGuardrails`
gate failed on the merged HEAD (§3.3/§7.1 above — durable on the plan branch; re-fires on resume or via
`--revalidate-task plan:guardrails`), OR every task passed but the opt-in end-of-run delivery to the
user's branch was **halted** (a `Conflict`, `DirtyWorkingTree`, or `HookRejected` `MergeOnSuccessResult`
— the work is durable on the plan branch, the user must finish the merge); for `graph --check`: the
diagram is stale or missing (the "regenerate" signal); for `lock --check`: the folder has drifted from
the baseline or the baseline is missing (the "re-baseline" signal); for `merge`: there are unresolved
conflicts to resolve, or the BASE baseline is missing and must be established first (§11.5) · `3`
cancelled.

**Plan-file → task-folder argument fixup** (all commands taking a plan folder as their first
positional: `run`, `validate`, `plan`, `graph`, `lock`, `merge`, `logs`). Before the folder's existence
is checked, the CLI applies one fixup so a user who passes the authored plan *source file*
instead of the generated *task folder* is not blocked: when the argument ends with `.md`
(ordinal, case-insensitive) **or** resolves to an existing file rather than a directory, and a
sibling directory with the same stem exists (`plans/0003-foo.md` → `plans/0003-foo/`), the
command silently switches to that folder and prints one info line
(`info: resolved plan file → task folder "<folder>"`). When no such sibling folder exists the
argument is passed through unchanged, so a genuinely bad path still produces the existing
`GR1001` "Plan folder does not exist" error (issue #16).

### 7.2 Definition-drift halt (issue #274)

Editing an already-`succeeded` task's definition and re-running must not silently reuse the stale cached
segment. A per-task **`definitionHash`** (§7 wire example above) makes such an edit observable, and on
resume the harness **halts honestly** — it neither silently reuses the old bytes nor silently re-runs
the changed task.

**What `definitionHash` covers.** The hash is computed over exactly the files that define one task's
behavior, in a fixed order: `task.json`; then the resolved **action file** (`TaskNode.Action.Path` — the
explicit `action.path` when set, else the convention-discovered single `action.*`, §3); then every file
under `tasks/<id>/guardrails/**` (recursive, sorted by relative path — this already includes each
deterministic guardrail's `<name>.json` metadata sidecar (§4.1), which lives inside that folder); then
every file under `tasks/<id>/preflights/**` (recursive, sorted by relative path). It is computed with the
same discipline as `PlanHash` (§7) — labeled segments, newline-normalized text (so CRLF/LF checkouts hash
identically), deterministic ordering, `sha256:`-prefixed — but at **task granularity** rather than
whole-plan.

**Two boundary calls (named, not hand-waved):**
- **Out of scope — a shared file OUTSIDE the task folder referenced by path in free prose.** If a prompt
  action names a repo file by path in its instructions, editing that file does NOT change any task's
  `definitionHash`. No mechanism resolves such free-text path references anywhere in the codebase today;
  `writeScope` (§3.4), `PlanHash` (§7), and the review marker (§13) all share this identical gap. It is
  documented here as a **known limitation**, not silently ignored.
- **Not in the per-task hash, but already covered elsewhere — plan-level `guardrails.json` settings.**
  `allowedTools`, `maxParallelism`, and `promptRunners.*` are NOT part of any per-task `definitionHash`;
  they are already inside the whole-plan `PlanHash` (§7), which already sets a plan-hash-mismatch signal
  on edit. That existing signal is currently **passive** — a mismatch warns loudly but lets the run
  proceed and reuse — a **narrower instance of the same "warn but reuse" bug class** this section closes
  at task granularity. Part A does **not** change the pre-existing `PlanHash` signal; the relationship is
  noted so the two are not confused.

**The `Guardrails-Task-Hash` trailer.** A task's integration commit carries a **third** trailer line,
`Guardrails-Task-Hash: <definitionHash>`, alongside the existing `Guardrails-Task: <taskId>` /
`Guardrails-Run: <runId>` (§5.3). Like them it is written on the plain FF'd commit as well as on merge
commits, so resume can read a task's recorded definition hash straight from the plan branch. It is
**backward-compatible**: omitted when the hash is unavailable (commits predating this field, fake
providers).

**The resume pre-pass comparison.** For **every** task the pre-pass is about to mark pre-settled-green —
whether from the journal (`status == "succeeded"`) OR from the plan-branch `Guardrails-Task-Hash` trailer
(§6.1) — the harness computes that task's **current** `definitionHash` and compares it to the recorded
one:
- **Recorded hash absent** (a journal entry or commit predating this field — i.e. an upgrade): treated as
  **"unknown — assume unchanged"** → match. Upgrading never forces a re-run storm on an unedited plan.
- **Match:** resume exactly as today — the task stays green, nothing is scheduled or re-run for it. Zero
  behavior and zero cost change for the common unedited case.
- **Mismatch on ANY task:** the harness schedules **nothing** this run. It returns
  **`RunReport.DefinitionDrift`** — a pre-DAG halt, a sibling of the existing `Abort`/`RunAbort` pattern
  (§5.3), rendered where `report.Abort` is rendered — with **exit code 2**, the actionable/needs-human
  bucket (matching the `planPreflights` / `planGuardrails` precedent, §7.1). It is **not** exit 1, which
  is reserved for genuine infrastructure faults.

**What the halt reports.** `DefinitionDrift` names, for each drifted task, its **old → new short hashes**
and its **transitive-descendant set** — the full DAG closure
`DependencyGraph.TransitiveDependentsOf(<taskId>)`. Full closure is the correct and cheap scope: a
changed producer can change a consumer's inputs; there is no finer per-state-key read contract anywhere
in the codebase to narrow it; and under the shared-worktree model a descendant already inherited the
ancestor's bytes as a git ancestor anyway. This set is **reported for the human's decision, not silently
re-executed** — auto-rerunning a fan-in descendant would fork it from a base that still contains its own
stale commit as an ancestor (the exact bug, one level down), so auto-invalidation is unsound. That
soundness limit is **why Part A halts** rather than auto-invalidating.

**The two remediation paths** named in the halt message:
- **`guardrails reset <folder> -y`** — a full rebuild; works today (after Part B, `reset` tears down the
  plan branch, §6.1).
- **`guardrails reset <folder> <taskId>...`** — a future **scoped** reset of only the drifted task(s) and
  their descendants, valid when the named set proves to be a safe trailing suffix of the plan-branch
  history. **Planned (Part C, a fast-follow), not yet shipped.**

---

## 8. Per-attempt log layout

```
logs/<runId>/<task-id>/attempt-N/
├── state-in.json            # the snapshot given to this attempt
├── attempt-provenance.json  # #198: model + segment worktree (branch + path) + base commit known at launch; absent for a serial script attempt
├── action-stdout.log / action-stderr.log
├── action-result.json
├── action-out-fragment.json # the harness-PROMOTED GUARDRAILS_STATE_OUT result (§9.5); a SCRIPT
                              #   action writes it directly, a PROMPT action writes a staging copy
                              #   the harness moves here immediately after the sub-agent exits
├── fragment.json            # copy of the fragment made on successful merge — audit trail
├── composed-prompt.md       # prompt ACTION: exactly what the runner got
├── claude-stream.jsonl      # prompt ACTION: raw runner output stream (canonical debug artifact)
├── transcript.md            # prompt ACTION: CLI-equivalent view, rendered deterministically from the stream (#27)
├── guardrail-<name>.stdout.log / .stderr.log   # script guardrail: captured output
├── composed-prompt.<name>.md                   # prompt guardrail: exactly what the verifier got
├── guardrail-<name>.stream.jsonl               # prompt guardrail: raw runner output stream
├── guardrail-<name>.transcript.md              # prompt guardrail: deterministic transcript projection
├── guardrail-<name>.verdict.json               # prompt guardrail: the verdict file (§4.2) — the ONLY pass/fail authority
└── feedback.md              # composed failure feedback (input to the NEXT attempt)
```

Prompt **actions** write `composed-prompt.md` / `claude-stream.jsonl` / `transcript.md`. Prompt
**guardrails** write the same three artifacts *per guardrail*, namespaced by the guardrail's
`<name>` (filename minus extension, §4): `composed-prompt.<name>.md`,
`guardrail-<name>.stream.jsonl`, `guardrail-<name>.transcript.md`, plus the
`guardrail-<name>.verdict.json` verdict file. Script guardrails write
`guardrail-<name>.stdout.log` / `.stderr.log`. The `<name>` is sanitized for the filesystem (any
character other than a letter, digit, `-`, `_`, or `.` becomes `_`).

As of issue #266, a prompt action's `action-out-fragment.json` and a prompt guardrail's
`guardrail-<name>.verdict.json` are written by the sub-agent to a per-attempt STAGING path and
PROMOTED here by the harness immediately afterward (§9.5) — never written directly to this
location by the sub-agent itself.

At the **task** level (`logs/<runId>/<task-id>/`, the parent of the `attempt-N/` dirs), a failed **union
re-verify** (a non-FF or AI-merge integration whose merged bytes fail the integration-guardrail set, §4.3)
persists its evidence BEFORE the B1 rollback discards the merged bytes (#188): one
`union-reverify-<guardrail>.stdout.log` per failing integration guardrail (its captured output) plus a
`feedback.md` describing the collision — the same `feedback.md` the task's needs-human summary points at
(previously that summary promised a `feedback.md` this path never wrote).

`transcript.md` (and each `guardrail-<name>.transcript.md`) is a PURE, DETERMINISTIC projection of
its `*.jsonl` stream (no model in the loop): assistant prose + `● Tool(args)` + truncated `⎿`
tool-result summaries + the final result text; thinking blocks and all telemetry (thinking-token
counters, rate-limit/init/usage events) are dropped. It is what a human skims and what a dependent
task's prompt links to (§9, #26) — the raw stream stays as the debug artifact.

---

## 9. Prompt runners

`promptRunners` (§2) maps names to runner configs. The `IPromptRunner` C# interface
quarantines all CLI specifics (flag spelling, output parsing). v1 ships `claude`:

- Invocation: `claude -p --output-format stream-json --verbose --permission-mode <m>
  --allowedTools <list> --max-turns <n> [--model <m>] [extraArgs…]`. The resolved `<m>` (issue #200)
  is the task's `task.json action.model` when the task declares one, else the runner's configured
  `promptRunners.<name>.model`, else `--model` is **omitted entirely** (the CLI's own default) — see
  §3 for the full precedence.
- Prompt delivered via **stdin** (no arg-length/quoting issues).
- cwd = the effective workspace (§5.1: the segment worktree in worktree mode, the plan workspace in
  serial mode — #134); `--add-dir <GUARDRAILS_PLAN_DIR>` grants access to state/verdict paths and
  names the MAIN checkout's plan dir even in worktree mode (the agent's cwd is the segment, but the
  harness-owned absolute state/verdict/log paths it must reach live under the main checkout — #134).
- The composed prompt (§8 `composed-prompt.md`) = body + appended harness sections:
  shared state (inlined ≤ 16 KB, else by path), **dependency context** (actions: pointers to
  the transitive `dependsOn` closure's `transcript.md` + contributed `fragment.json`, present
  on every attempt — #26 Gap 4), output contract (actions), previous-attempt feedback (actions,
  attempt ≥ 2: the latest `feedback.md` verbatim + pointers to ALL prior attempts' transcript
  and feedback — #26 Gaps 2 & 3, "fix these specific problems; do not start over"), **staging-outputs
  contract** (actions, when `stagingOutputs` declared, §3.5: the absolute `GUARDRAILS_STAGING_DIR` and
  the `from→to` map embedded verbatim — "write here; the harness moves it to `.claude/`; do not write
  `.claude/` directly", since agents read instructions, not env vars), verdict
  contract (guardrails: "you are a verifier — do NOT fix anything").
- Semantic success for a prompt **action** = process completed AND result `is_error == false`.
  For a prompt **guardrail** = the verdict file, full stop.
- Per-attempt `total_cost_usd` is recorded in the journal. The `run` summary and
  `guardrails status` print a final `Total prompt cost: $X.XXXX` line summing every
  recorded attempt's `costUsd`; the line is omitted entirely when no attempt recorded a
  cost (deterministic-only plans stay noise-free).
- `guardrails validate` probes each DECLARED runner's `command` on PATH and emits a
  **warning** (GR2009) if it does not resolve — not an error, since the plan may run on
  another machine where the runner is installed.
- A prompt action may signal an unresolvable decision by writing
  `{ "needsHuman": "<question>" }` into its fragment — the harness treats the attempt
  as needs-human immediately (no retry burn).

**`needsHarnessWrite` — harness-mediated write escape hatch for `.claude/` (issue #191).** In
worktree mode, a task action running as a Claude Code subprocess can **never** write under
`.claude/` — the runtime's tool-permission layer refuses `.claude/` writes unconditionally in a
fresh, never-interactively-approved segment worktree (broader than the new-subdirectory-only gap
issue #101 fixed: this affects EXISTING files too), and the refusal survives every write mechanism
including `dangerouslyDisableSandbox`. `needsHarnessWrite` is a second structured escape hatch,
parallel to `needsHuman`, that lets the action ask the **.NET harness process itself** — not
subject to Claude Code's tool-permission layer — to perform the write on its behalf:

```jsonc
{ "needsHarnessWrite": { "path": ".claude/skills/guardrails-review/SKILL.md", "content": "...", "reason": "..." } }
```

- **Wire contract.** A root fragment key, read from the SAME already-written `GUARDRAILS_STATE_OUT`
  file `needsHuman` uses, via the same "read once" shape. `path` is workspace-relative (the same
  convention `writeScope` entries use — the segment worktree in worktree mode, the plan workspace in
  serial mode); `content` is the literal file content; `reason` is optional and human-readable.
  **Singular only in v1** — one harness-write per attempt (the issue's own example shows a single
  object, not an array). A task needing multiple `.claude/` files touched does so across multiple
  attempts/retries; this is a documented v1 limitation, not solved here.
- **Coexistence with a state fragment.** The key is CONSUMED (stripped from the fragment) before the
  normal fragment-merge validation runs, so any OTHER top-level key the action ALSO wrote (its own
  state contribution, keyed under its own task id) merges normally in the same attempt — a task can
  request a harness write AND contribute state together. This differs from `needsHuman`, which is a
  full short-circuit (no guardrails, no merge at all): `needsHarnessWrite` unblocks write MECHANICS
  only, never verification — the task's guardrails still run afterward. If a fragment carries BOTH
  `needsHuman` and `needsHarnessWrite`, `needsHuman` wins (checked first; a human-decision halt
  trumps a mechanical write request).
- **Two load-bearing safety checks, BOTH run BEFORE the write (a security boundary — otherwise any
  task could claim "I'm blocked, please write this for me" and bypass `writeScope` entirely):**
  1. **Workspace-escape check — ALWAYS runs, independent of `writeScope`.** Reuses
     `WorkspaceContainment.Escapes` (the same "does this path escape the boundary" predicate used
     elsewhere). An absolute path or a `../` climb-out is rejected even for a task with NO declared
     `writeScope` — the segment-worktree containment is the boundary in that case.
  2. **`writeScope`-membership check — only when the task DECLARES a `writeScope`.** Reuses
     `WriteScope.IsInScope` — the SAME scope-matching predicate the post-hoc write-scope CHECK (§3.4)
     uses, so the two enforcement points can never drift. **A task with NO `writeScope` declared
     allows the write unconditionally**, mirroring §3.4's "Absent ⇒ no check" for the retrospective
     check — the segment-worktree containment + the worktree-containment hook (§9.4) are the
     backstops in that case.
  A rejected request fails the attempt with actionable feedback naming the offending path (retries;
  eventual `needs-human` on budget exhaustion) — the same shape as an out-of-scope write-scope
  violation. A request that PASSES validation but whose actual write fails (disk full, a genuinely
  unwritable location even for the harness process) is likewise treated as a failed attempt with
  actionable feedback, never a crash.
- **After the write, normal gating resumes.** A successful `needsHarnessWrite` falls through to the
  SAME write-scope CHECK (§3.4, if the task declares one — the harness-written file is now part of
  the segment's git diff too; this is expected, not redundant — the prospective check prevents the
  attempt from even TRYING an out-of-scope write, the retrospective check is unchanged defense in
  depth) and then the task's own `guardrails/`, exactly as any other successful action does.
- **Failure classification (runner-agnostic).** A non-success prompt result is classified into a
  `PromptFailureKind` — `Transient` | `OutputCap` | `MaxTurns` | `Timeout` | `Error` — by the runner
  CLASS, which is the SOLE home of the fragile vendor error-string matching (a 429/503/529 status, an
  "overloaded" / rate-/session-/usage-limit phrase, the "…output token maximum" message, the
  `error_max_turns` subtype / "Reached maximum number of turns" message). The harness routes on the
  ENUM only, never on a CLI-specific string. Matching prefers a structured signal (HTTP status, the
  `error_max_turns` terminal subtype) over free text, and a miss is conservative (→ `Error`, which
  consumes the budget — never a false `Transient` that could loop).
  - **`Transient`** (issue #115): a retryable infra condition. Does **NOT** consume the retry budget —
    the harness backs off (bounded exponential, honoring a parsed reset hint for display) and re-runs
    the same attempt, bounded by `transientPauseBudgetSeconds` (§2). A `PromptPaused` observer event is
    emitted; a transient pause is never journaled unless its budget is exhausted (→ `rate-limited`).
    The signal is read from the terminal `result` error text OR, when there is no terminal result (the
    instant-rejection case), the captured process stdout/stderr — both inside the runner quarantine.
  - **`OutputCap`** (issue #114): consumes the budget like `Error` but composes actionable feedback
    ("write incrementally / split; or `needsHuman` if inherently too large") and records the distinct
    `output-cap` outcome (§7).
  - **`MaxTurns`** (issue #129 / #94): the agent exhausted its TURN budget mid-progress (the
    `error_max_turns` subtype). Consumes the budget like `Error` but composes "work directly toward the
    deliverable; or `needsHuman` if under-budgeted" feedback, records the distinct `max-turns` outcome
    (§7), AND **auto-escalates the next attempt's `maxTurns`** (1× → 1.5× → 2.25× …, capped 4×, rounded
    up — the same shape as the timeout clock) so the retry has turn headroom instead of re-hitting the
    same cap. The feedback is **mode-aware** (issue #167): serial mode keeps "continue from the preserved
    partial work"; worktree mode discloses the segment reset / file-write rollback and instructs
    re-authoring.
  - **`Timeout`** (issue #119): records `timeout` (§7), composes mode-aware feedback (serial: "continue
    from preserved partial work"; worktree: "your file writes were rolled back — re-author", issue #167),
    and the retry's clock is extended.
- **Observer signal.** `IRunObserver.PromptPaused(task, reason, backoff, pauseCount)` surfaces a
  transient pause so an operator sees a HEALTHY task waiting out a limit, not a failing one. Default
  no-op (non-CLI observers need not handle it).
- **Scope.** Classification + transient pausing apply to the prompt **action**. A transient signal hit
  by a prompt **guardrail** still surfaces as that guardrail's normal verdict failure (the verifier's
  signal is its verdict file); promoting guardrail-prompt transients to the same pause path is a future
  extension, not part of #114/#115/#119.

### 9.1 AI-merge worker

The AI-merge worker resolves a git merge conflict during a union (§5.3 case B). It is a **constrained
prompt action behind `IPromptRunner`** (the same seam as `claude`). **The existing `IPromptRunner`
contract returns metadata only** (`PromptResult` = `{Completed, IsError, ResultText, CostUsd,
NumTurns, Summary}`) — **there is no byte channel.** So the worker uses the existing **on-disk file
convention** (the runner writes a file, the harness reads it) via a **NEW merge env contract**, and a
**distinctly named merge prompt profile** (NOT a `guardrailOverrides`-shaped profile — that is a
guardrail-verifier concept). **It is a BYTE PRODUCER, never a VERDICT PRODUCER:**

- **Merge env contract (new):** `GUARDRAILS_MERGE_BASE`, `GUARDRAILS_MERGE_OURS`,
  `GUARDRAILS_MERGE_THEIRS` (the three-way inputs on disk) and `GUARDRAILS_MERGE_OUT` (the path the
  worker writes the resolution to). The harness reads `GUARDRAILS_MERGE_OUT` after the run. These four
  files live in a harness temp dir that is **granted to the runner's sandbox** (the runner's cwd is
  the integration worktree, so a temp dir outside it would otherwise be unreachable — the resolution
  could not be written and `GUARDRAILS_MERGE_OUT` would stay empty). The same four **absolute paths
  are embedded verbatim in the composed prompt body**, not just the env-var names (agents read
  instructions, not env — §5.1). The temp dir stays OUTSIDE the worktree so it never pollutes
  `git status` or the merge commit.
- **Input:** the conflicted files (with markers) + base/ours/theirs on disk, and the colliding
  upstream tasks' intents (their `task.description` + `writeScope`) composed into the prompt string.
- **Output:** the merged bytes only, written to `GUARDRAILS_MERGE_OUT`. A rationale is logged
  (NON-gating, never read as a verdict). `PromptResult.IsError` and the exit code are **not** the
  verdict.
- **Trust:** three deterministic checks — (i) the resolution is non-degenerate: an empty or
  whitespace-only `GUARDRAILS_MERGE_OUT` is a FAILED attempt (an empty resolution would otherwise
  pass gates ii/iii vacuously and silently blank the conflicted file); (ii) no conflict markers
  remain (`git diff --check`); (iii) blast-radius (modified only the git-reported-conflicted files,
  `git status --porcelain`). A violation ⇒ discard (`reset --hard`) + `needs-human`.
- **Budget:** 1 retry (2 attempts). Escalate to `needs-human` on markers-left / out-of-bounds /
  re-verify-fail / budget. The AI's exit code is never a verdict.

Its cost is charged against `maxCostUsd` like any prompt attempt. It is configured under
`promptRunners` as a **reserved merge runner profile** (e.g. `ai-merge`) — a distinct merge profile
named for what it is (read the conflict, write only `GUARDRAILS_MERGE_OUT`), **not** a
`guardrailOverrides` block.

### 9.2 AI triage on needs-human (plan 08 §9, PO #7 / Decision 8)

When a task exhausts its retry budget and transitions to `needs-human`, the harness optionally
runs a **one-shot advisory triage step** to classify the root cause. It is a **constrained prompt
action behind the existing `IPromptRunner` seam** (the same seam as `claude` and the AI-merge
worker), invoked under a **distinct `ai-triage` prompt profile** in `promptRunners` — not a
`guardrailOverrides` block. `NeedsHumanTriage` is the class that owns the step.

**Trigger — exhaustion only.**

- Triage fires **ONCE** on the **terminal exhaustion transition** (all retry budget consumed by
  action/guardrail failures across every attempt).
- It does **NOT** fire when the agent itself emitted `{"needsHuman": "..."}` (that is already a
  human ask — the question is already posed; additional triage is redundant and would race).
- It does **NOT** fire mid-retry (between attempts while budget remains).

**Diagnosis — tool-vs-local.**

Given the failed task (`task.json`, every attempt's action output, the failing guardrail outputs,
and the run context), the triage prompt classifies the root cause as one of:

- `guardrails-tool` — a Guardrails harness or tooling limitation; warrants a GH issue against
  the Guardrails repo. The triage response includes a ready-to-file `ghIssueTitle` + `ghIssueBody`.
- `local-repo` — a problem with the plan, code, or tests for the **current** repo; no Guardrails
  issue is warranted. The triage response includes an `analysis` field.

**`feedback.md` — TASK-LEVEL under the elevated logs.**

Triage writes `logs/<runId>/<task-id>/feedback.md` — a **sibling of the `attempt-N/` directories**,
NOT inside any attempt dir. This is distinct from the **per-attempt** `feedback.md` written by §8
(which lives at `logs/<runId>/<task-id>/attempt-N/feedback.md` and is the retry's input). The
task-level `feedback.md` captures:

- The diagnosis (`guardrails-tool` or `local-repo`).
- The evidence the triage drew on.
- For a `guardrails-tool` diagnosis: the drafted GH-issue title and body.

**Structured `triage.json` sidecar — for the console summary (issue #163).**

When the triage output is the structured JSON above, triage ALSO writes a compact, machine-readable
sibling `logs/<runId>/<task-id>/triage.json` — `{ "diagnosis", "summary", "ghIssueTitle"? }` — next
to `feedback.md`. `summary` is a one-line diagnosis distilled from `ghIssueTitle` (tool problems) or
`analysis` (local problems); `ghIssueTitle` is present only for a `guardrails-tool` diagnosis. The
sidecar lets the `run` summary surface the **root-cause category + one-line** per needs-human task
directly in the console (and annotate tasks that share a category) without the user opening each
`feedback.md`. It is **advisory and additive**: an unstructured or failed triage writes no sidecar,
and the summary then falls back to the `feedback.md` pointer alone. The sidecar never gates anything
and is never read as a verdict.

**Needs-human message pointer.**

The task's `needs-human` message (surfaced in the run summary and `guardrails status`) references
the `logs/<runId>/<task-id>/feedback.md` path so the human lands on the triage diagnosis
immediately.

**Strictly advisory — gates nothing.**

The task is **already `needs-human`** before triage runs; triage can **never** change that verdict,
re-open the task, mark it done, or burn retry budget.

- `PromptResult.IsError` and the runner exit code are **never** read as a verdict.
- A thrown exception or a runner error means "no `feedback.md` was produced"; it is logged and the
  task remains plainly `needs-human`. Triage must **never** block or abort the run — all other
  independent tasks continue normally.

A prompt proposes, a file certifies: only a written `feedback.md` provides the diagnosis; a
failed/throwing triage is silently skipped.

**Opt-in auto-file (`triageAutoFile`, default OFF).**

By default, triage only **drafts** the GH issue (title + body) into `feedback.md` and files
**nothing** to a remote. Only when `triageAutoFile` is explicitly opted in — gated behind a
configured GH repo + token — does the harness auto-file the issue. Default is **OFF**.

### 9.3 Permission-wall early halt (issues #86 / #104)

When the runner REFUSES a write/edit because the target path is not on the granted permission
allow-list, retrying cannot clear it — switching tools or re-issuing the same write hits the same
refusal. The harness detects this **permission wall** and settles the task `needs-human` EARLY with
the distinct `permission-denied` attempt outcome (§7), instead of spending the rest of the retry
budget on the identical, un-recoverable wall.

**Runner-agnostic signal.** Detecting the concrete refusal is **quarantined in the runner CLASS** (the
SOLE home of the vendor permission-denial wording, like the §9 failure classifier): for `claude`, the
wall surfaces in the `tool_result` events of the `stream-json` stream, NOT the terminal `result`
message — a refusal under `acceptEdits` does not make the agent report `is_error`, so the agent keeps
trying workarounds and burns turns/retries (exactly the #86/#104 waste). The runner mines the distinct
**refused write paths** (extracting the path the denial message embeds, falling back to the preceding
write-family `tool_use`'s `file_path` when the message carries none) and returns them as a
runner-agnostic list. The harness routes on the LIST of paths only — never on a vendor string.

**Two halt rules.**

- **Structural `.claude/` path (issue #104).** The Claude Code sub-agent runtime blocks automated
  writes under `.claude/` **even when `permissionMode` is `acceptEdits`**, so NO number of retries can
  clear it. One refusal on a `.claude/` path settles `needs-human` on the **FIRST** attempt that hits
  it — zero retries wasted.
- **Repeated same path (issue #86).** Any other path refused on **two or more** attempts is a
  structural blocker the agent cannot fix by retrying. The harness halts on the **second** attempt that
  re-hits the SAME path, rather than spending the rest of the budget on the identical wall. A path
  refused **once** does NOT halt (the retry is given its chance — a one-off block the retry clears is
  normal retry behaviour).

**`feedback.md` — task-level remediation.** The halt writes a `feedback.md` naming the exact blocked
path(s) and the concrete fix: for a `.claude/` wall, grant `Write(.claude/**)` (and `Edit(.claude/**)`)
in the project's `.claude/settings.json` allow-list, **or** re-target the task to write its deliverable
to a staging path OUTSIDE `.claude/` and move it into place with a follow-up script step (which the
harness runs with full permissions); for any other repeated path, confirm the runner's `permissionMode`
/ `allowedTools` and the `.claude/settings.json` allow-list cover the path, then re-run (the harness
resumes from here).

**Residual (honest scope).** This is a **detect-and-halt-honestly** mitigation: it ends the #86/#104
retry-budget waste and lands the human on an actionable diagnosis on the first (structural) or second
(repeated) attempt. It does **not** itself grant `.claude/` write access — the root cause is a
Claude-Code-runtime restriction the harness cannot override from outside the sub-agent. Issue #266
removes one further trigger of this rule structurally: the harness's own default STATE_OUT/VERDICT_OUT
targets are never `.claude/`-nested from the sub-agent's point of view, regardless of where the plan
folder itself lives (§9.5) — so this halt rule's remaining scope is exactly a task-declared `.claude/`
write that skipped `stagingOutputs`.

The full autonomous fix is the `task.json` `stagingOutputs` contract (§3.5, issue #130): a task
declares the `.claude/` deliverable it produces and a staging path the action writes instead, and
the harness moves the staged output into its real `.claude/` path after the action succeeds and
before guardrails run — so the task completes unattended. The §9.3 detect-and-halt remains the
safety net for a `.claude/`-writing task that did **not** declare `stagingOutputs`; its
`feedback.md` now points at `stagingOutputs` as the fix. The breakdown-time emission of
`stagingOutputs` for `.claude/`-targeted tasks (Option C) is a `plan-breakdown` skill change,
tracked separately.

### 9.4 Worktree-containment PreToolUse hook + git-stash safety (issues #199 / #192)

Worktree isolation (§1) is a physical-tree boundary, but until #199 nothing at RUNTIME stopped a
prompt agent from writing to an absolute path OUTSIDE its own segment worktree — a write there never
appears in the segment's own `git diff`, so the write-scope CHECK (§3.4, the **INNER**, post-hoc
boundary) never sees it and it goes completely undetected. #199 adds an **OUTER**, hard-enforced
runtime boundary: for every worktree-mode prompt invocation (action OR guardrail — a verifier prompt
is still an agent that can call `Write`/`Edit`/`Bash`), the harness generates a Claude Code
**PreToolUse hook** and injects it via `claude -p --settings <path>` (session-scoped — never touches
the user's own `~/.claude/settings.json` or the repo's `.claude/settings.json`). `--settings` is
**absent** in serial/shared-workspace mode: there is no isolated segment tree to contain writes to.

- **Generation.** `Guardrails.Core.Prompts.WorktreeContainmentHook.WriteHookFiles(logDir,
  worktreeRoot)` writes two files into the attempt's **log dir** (`logs/<runId>/<task-id>/attempt-N/`
  — harness-owned, OUTSIDE the segment worktree, so the generated files never pollute `git status` /
  the write-scope diff): an OS-picked hook script (`containment-hook.ps1` on Windows,
  `containment-hook.sh` on Unix — the segment worktree root is baked into the script as a literal, one
  script per attempt, no extra env/arg plumbing) and `containment-settings.json` (one `PreToolUse`
  matcher group covering `Write|Edit|MultiEdit|NotebookEdit|Bash`, one `command` hook pointing at the
  script). `ActionRunner`/`GuardrailRunner` append `--settings <path-to-that-file>` to the invocation's
  `ExtraArgs` whenever a real segment worktree is present.
- **Interception mechanism.** The hook reads the PreToolUse tool-call JSON from stdin (`tool_name`,
  `tool_input.file_path`/`notebook_path` for Write/Edit/MultiEdit/NotebookEdit, `tool_input.command`
  for Bash). Exit code 2 + a stderr message is Claude Code's documented block contract; exit 0 allows
  the call. The path-escape decision REUSES `WorkspaceContainment.Escapes`'s rule (rooted-path
  rejection + normalized-path directory-boundary comparison against the worktree root) — re-expressed
  in shell/PowerShell (the hook runs as an OS process Claude Code spawns directly, not a .NET
  callback), never a DIFFERENT rule. For `Bash`, the script heuristically extracts a target path from
  write-ish forms — output redirection (`>`/`>>`), `tee`, `cp`/`mv`, `git checkout -- <path>`, `git
  worktree add <path>` — and applies the same escape check to whatever it can parse out.
  **Both scripts are pure, dependency-free string-based `.`/`..` segment normalization — NEITHER
  resolves symlinks, and neither calls an external `realpath`/`readlink`.** (An earlier version of
  the bash script shelled out to `realpath -m` to also resolve symlinks; `-m` is GNU-coreutils-only,
  so on macOS's BSD `realpath` the call silently misbehaved and escape detection went dark —
  13 macOS-only CI failures, all "expected block, got allow." The fix dropped the external dependency
  entirely rather than chase a portable flag: both platforms now implement the identical rule,
  in-process, with no core-utils-flavor dependence.) The no-symlink-resolution gap is therefore
  **consistent across platforms** — a known, accepted limitation, not a macOS-specific regression.
- **git-stash safety (#192), same mechanism, additive rule.** `git stash`'s stack (`refs/stash`) is
  repo-wide, not per-worktree: concurrent worktree-mode tasks (or a human's own diagnostic worktree)
  independently reaching for `git stash`/`git stash pop` around the same time can grab the WRONG
  entry — silently applying one worktree's uncommitted changes into a different one (this happened
  twice in the dogfood run that raised #192, recovered via `git fsck --unreachable`). The SAME hook
  script blocks the entire `git stash` family (`push`/`pop`/`apply`/`list`/…) unconditionally in
  worktree mode, regardless of any path argument — one mechanism, two additive checks, not two
  separate hooks. **Defense in depth**: the harness-contract context every worktree-mode prompt
  invocation receives (the same injection point as the state/output/needsHuman contract,
  `PromptComposer.ComposeAction`/`ComposeGuardrail`, gated on `isWorktreeMode`) ALSO appends a
  `## Worktree safety` section warning that `git stash` is not safe here and giving the local,
  stash-free alternative: `git diff > /tmp/mine.patch` then `git checkout -- <files>` to test the
  baseline, then `git apply /tmp/mine.patch` to restore. The advisory section explains WHY before the
  agent ever tries; the hook is the hard backstop if it tries anyway.
- **Boundary / honesty note.** This defends at the TOOL-CALL layer Claude Code exposes
  (Write/Edit/MultiEdit/NotebookEdit/Bash). The `Bash` matcher is a heuristic over the command TEXT,
  not a sandboxed OS-level filesystem ACL — it cannot stop an agent from asking Claude Code to spawn
  an arbitrary process that itself writes outside the worktree via a form the heuristic fails to
  recognize (a compiled helper, an obscure redirection form, a script interpreter's own file-write
  primitive). It raises the bar sharply against the classes of accidental/careless escape #199 was
  written against (an absolute path typo, a "let me just check the sibling task's tree" excursion, a
  stash collision); it is not a security sandbox against a deliberately adversarial agent. The
  write-scope CHECK (§3.4) remains the INNER, unaffected boundary underneath it.

### 9.5 Prompt-output staging: STATE_OUT / VERDICT_OUT never target the plan folder directly (issue #266)

A plan folder nested under `.claude/` (a natural place to keep one — `/plan-breakdown` puts the
task folder beside its source `.md`, and this repo's own plan docs already live under
`.claude/plans/`) puts `logs/<runId>/<task>/attempt-N/…` under `.claude/` too — the harness's
OWN default targets for `GUARDRAILS_STATE_OUT` (§5.1, §6.2) and `GUARDRAILS_VERDICT_OUT` (§4.2).
Before #266, a prompt action/guardrail's own Write tool call to either target hit the SAME
structural `.claude/` sensitive-path block §9.3 describes for a task's own deliverable — except
`stagingOutputs` (§3.5) could not help here, because these are harness-internal, task-oblivious
targets that exist on EVERY prompt attempt regardless of what the task declares; there is nothing
for a plan author to opt into.

**The fix generalizes the `stagingOutputs`/#130 pattern to the harness's own default targets,
unconditionally, for every PROMPT action/guardrail** (never scripts — a script subprocess has no
Claude Code tool-permission layer to be blocked by, so its target is the documented path directly,
byte-for-byte as before): the sub-agent is handed a per-attempt STAGING path —
`<effectiveWorkspaceRoot>/.guardrails-agent-io/<task-id>/attempt-N/<final-filename>` — for
`GUARDRAILS_STATE_OUT`/`GUARDRAILS_VERDICT_OUT` (embedded in the composed prompt text AND the env
var, so the two never disagree), and the harness (`Guardrails.Core.Execution.PromptOutputStaging`)
promotes the staged file to its documented final location the INSTANT the sub-agent process exits
— before the needsHuman/needsHarnessWrite fragment reads (§9), before the verdict read (§4.2),
before anything else in the attempt pipeline touches it.

**Why this staging location, not system TEMP (unlike `GUARDRAILS_MERGE_OUT`, §9.1).** The AI-merge
worker's MERGE_OUT staging under system TEMP works because that invocation never receives the
worktree-containment hook (§9.4) — nothing there enforces "stay inside the worktree." A prompt
action/guardrail DOES receive that hook, which blocks any Write/Edit/Bash target resolving OUTSIDE
`worktree.WorktreePath`. Routing through system TEMP would trade the `.claude/` block for a
self-inflicted containment-hook block. `.guardrails-agent-io/` is a plain dot-folder INSIDE the
effective workspace root (the segment worktree in worktree mode, the plan `workspace` in serial
mode — mirroring `.guardrails-staging/`'s own placement exactly), so it satisfies BOTH constraints
without any change to `WorktreeContainmentHook` and without a new `--add-dir` grant.

**Cleanup.** Because the promote step MOVES (never copies) the one expected file and nothing else
is ever written under that leaf directory, the harness also deletes the whole per-attempt staging
subtree afterward (belt-and-braces, mirroring `StagingMover`'s "delete the whole tree" idiom) — no
`.gitignore`/`.git/info/exclude` entry is needed (git never tracks empty directories), unlike
`stagingOutputs`.

**Interaction with `needsHarnessWrite` (#191).** Unaffected and complementary, not overlapping:
`needsHarnessWrite`'s own write is already performed by the .NET harness process directly
(`AtomicFile.WriteAllText`), never through the sub-agent's tool-permission layer — this fix is a
PREREQUISITE for it, not a duplicate: before #266, a `.claude/`-nested plan folder could not even
get a fragment written at all, so a `needsHarnessWrite` request embedded in that fragment was
unreachable. After #266, `needsHarnessWrite` becomes usable for `.claude/`-nested plans too.

**Interaction with §9.3's permission-wall halt.** The "structural `.claude/` path" halt rule used
to fire — correctly, but for the wrong reason — on the harness's OWN fragment/verdict targets
whenever the plan folder itself was `.claude/`-nested. After this fix, that trigger no longer
exists: the sub-agent is never handed a `.claude/`-nested target for its OWN STATE_OUT/VERDICT_OUT
again. §9.3's halt rule now fires only for its originally-intended scope — a task-declared
`.claude/` write that did not use `stagingOutputs`.

---

## 10. Diagram artifacts (`diagram.md` + `diagram.html`)

`guardrails graph [folder]` renders the plan's task/guardrail DAG as a Mermaid
`flowchart TD`, using the **container model** (design-of-record 09-preflight-first-class),
and writes two companion files:

- **`diagram.md`** — the GitHub render artifact: a provenance comment + fenced Mermaid
  block + structure-only caption. GitHub renders it inline.
- **`diagram.html`** — the local-navigation companion: a self-contained pan/zoom/fullscreen
  HTML viewer whose task/check nodes carry `click href` directives pointing to their
  source under the plan folder. Use `--no-html` to suppress it; a missing HTML file is **not
  treated as stale** by `--check`. Node clicks require serving the file via a local HTTP
  server (`python -m http.server`) — browsers block `file://→file://` navigation by default.
  The `click href` directives are HTML-only: `diagram.md` stays click-free (GitHub sandboxes
  Mermaid; the targets are `file://`-local). Assets load from CDN (needs internet once);
  offline inlining is a v2 consideration.

Both files are **generated, non-authored artifacts**: NOT part of the plan contract, safe to
delete and regenerate, and excluded from `guardrails.baseline`. Nothing is added to
`guardrails.json` or its model — the staleness key lives in the diagram files instead.

**Shape — the container model.** Each task is a self-contained `subgraph task_<id>["<id>"]`
container holding its preflight and guardrail check nodes as small boxes drawn **directly
inside** the container — there are no bare check nodes outside a container, and (as of the
nested-box removal simplification below) no nested `Preflights`/`Guardrails` wrapper subgraph
either. Two more subgraphs bracket the **whole DAG** and are **always emitted**, even when
their folder is empty, because they are structural brackets, not conditional content:
`plan_preflights["Full Flight Checks"]` at the TOP (the plan-level `<plan>/preflights/`
folder) and `plan_guardrails["Terminal Gate"]` at the BOTTOM (the plan-level
`<plan>/guardrails/` folder) — these two are **unaffected** by the nested-box removal: they are
one-off heterogeneous brackets on the whole DAG, not a per-task repeated pattern. Retry /
feedback (cyclic) edges remain out of scope for v1.

**Nested boxes dropped (simplification).** A task container previously nested a "Guardrails"
sub-container (and, when present, a "Preflights" sub-container) around its leaf check nodes.
This nesting-within-nesting made a real generated diagram look busy for no semantic gain: the
wrapper subgraph id was never referenced by edge emission, container styling, or
`source-sha256` — purely cosmetic. Leaf check nodes are now emitted as direct children of the
task container; the existing `:::preflight`/`:::guardrail` `classDef` fill remains the only
visual category distinction. **Emission-order contract (load-bearing, tested):** because the
nested boxes used to convey "preflights run before, guardrails run after" visually, that
temporal fact is now preserved by a GUARANTEED emission order — a task's preflight check
node(s), if any, are always emitted BEFORE its guardrail check node(s) within the container.
This is a stable, tested convention, not a rendering accident, and callers may rely on it.

**Every check node's drawn label is its short, stable `name` — never its `description`, and never
truncated.** An earlier version drew a task-level preflight's full descriptive text (which can run
to many words — it documents a specific dependency-delivery precondition) and truncated it to a
word-boundary cut around 40 characters so it wouldn't dwarf the rest of the diagram. That
truncation was scoped to task-level preflights only; guardrail check nodes and plan-level check
nodes still drew their full, untruncated `description`, which could be equally long (a guardrail's
`description` documents the specific gaming vector it catches, per the `catches:` authoring
doctrine — legitimately detailed content). The fix (issue #222): draw every check's `name` — the
file-derived identifier (e.g. `01-core-tests-green-excluding-target`), already short, stable, and
matching the file the node's own click target opens — uniformly for every check kind at both
scopes. No truncation heuristic is needed anywhere now. The FULL `description` (falling back to
`name` when absent) is never lost — it remains reachable via the SAME `click` directive mechanism
`diagram.html` already uses for every node (source-file click-through, issue #33): the tooltip
argument of every check's `click` directive carries the full description.

**Legend — static content OUTSIDE the Mermaid graph.** A Mermaid-native legend (a disconnected
subgraph of dummy colour-swatch nodes) was prototyped and rendered BROKEN headless against the
bundled `mermaid@11.4.1`: dagre lays out a disconnected subgraph as a phantom extra "task"
overlapping the real DAG. The only approach that renders correctly is content entirely outside
the Mermaid source: `diagram.md` carries a plain Markdown legend block placed immediately after
the structure-only caption (itself after the closing ` ```mermaid ` fence) — GitHub's Mermaid
sandbox has no overlay-content option, so a plain Markdown block is the only placement that
reads correctly there; `diagram.html` carries a corner-anchored HTML overlay `<div id="legend">`
(`position: fixed`), mirroring the existing `#bar`/`#hint` overlay divs. Both state the SAME
wording: the colour mapping AND the before/after timing/consequence — a bare category name
would not preserve the ordering semantic the removed nested boxes used to convey visually:

- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry (dependency-delivery
  precondition)
- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish
- 🟢 Plan-level containers ("Full Flight Checks" top, "Terminal Gate" bottom) run the same two
  checks once for the whole plan, at the very start and very end.

**The legend is excluded from `source-sha256`** — same treatment as the existing cosmetic
`classDef` color lines (append-only in `Render`/`RenderInteractive`'s callers, never inside
`SemanticContent`). Getting this wrong would make `graph --check` report every plan as
spuriously stale on a legend WORDING edit alone.

**Edges clip to the container border (`subgraph --> subgraph`).** The DAG is drawn directly
between container ids — `task_A --> task_B` for each task B that `dependsOn` task A; the
`plan_preflights` container points into every DAG-root task's container (a task with no
`dependsOn`); every DAG-leaf task's container (a task nothing depends on) points into the
`plan_guardrails` container. Because the edge references the container's own subgraph id, the
bundled Mermaid (`mermaid@11.4.1`, CDN-pinned in `diagram.html`) clips the arrow to the
container's **outer border**, like an ordinary box-to-box flowchart edge — the line never
pierces the box (issue #210). This replaced an earlier interior-anchor technique (one invisible
`<container>_anchor` node per container, edges drawn anchor→anchor) that a prior Mermaid version
required but which drew every edge to a point ~65px *inside* the box; rendering both forms
headless against 11.4.1 confirmed the direct form lands on the border while the anchor form
pierced. Container "kind" fill (task vs. plan-level) is applied per container via a
`style <id> fill:…,stroke:…,color:…;` statement, **not** a `class <id> <className>;` assignment:
in 11.4.1 a `class` assignment does not reach a subgraph that is itself an edge endpoint — and
every container is one — whereas `style <id>` colours it. `style <id>` also colours an **empty**
plan-level bracket, which Mermaid renders as a plain node (not a cluster) — so the Full Flight
Checks / Terminal Gate brackets keep their colour even when their folder is empty.

**A task container's click target is a POST-RENDER SVG overlay on its title band, NOT a Mermaid
mechanism at all (issue #211's anchor-node fix superseded; issue #235).** The #210 edge fix above
only changed how DAG EDGES attach to a container; it does NOT make the container itself clickable.
Real headless-Chrome verification against the bundled `mermaid@11.4.1` — clicking the container
body, its title text, and its fill rect, then checking whether a real navigation (a popup) actually
occurred — proved a `click` directive targeting a subgraph/cluster id **never fires**: Mermaid wraps
a clickable LEAF node in a real `<a href>` element (confirmed firing), but never wraps a
`<g class="cluster">` (subgraph) in one, regardless of what id the `click` directive names. This is a
genuine, still-open upstream Mermaid limitation: mermaid-js/mermaid#1637 ("Let subgraph handle
clicks") and #5428 ("click action for subgraphs") are both open feature requests.

*Why the first fix (issue #211, an invisible anchor NODE) was insufficient.* That fix added one
`{containerId}_anchor[" "]:::invisible` node per container and pointed the container's `click`
directive at it instead of the subgraph id — which DOES fire (Mermaid wraps it in a real `<a href>`,
like any leaf node) but proved USELESS in practice: dagre (Mermaid's layout engine) sizes a
`[" "]`-labelled node to a tiny default (~39×20px) and packs it wherever ITS OWN layout algorithm
decides — for a container with several guardrail leaf boxes packed side-by-side, that is a thin
sliver squeezed into whatever gap remains, not centered and not where a user would naturally click.
Measured on a real 4-guardrail task container: the anchor covered 0.44% of the container's area, in
a narrow strip near the container's right edge, and none of 4 realistic click points (dead-center,
near-title, left-margin, bottom-strip) landed on it — dead-center instead landed on a leaf
guardrail box's own click target and opened THAT guardrail's source file instead of the task
folder. Forcing the anchor wider via a padded label does not fix this either (verified): dagre still
packs it into its own slot rather than centering or spanning it, and a content-dense container has
almost no empty background region to reliably overlay in the first place. This whole "shape the
anchor node's content to control its size/position" direction was abandoned as unfixable via any
Mermaid-source anchor-node mechanism.

*The fix: a title-band overlay injected via JavaScript AFTER Mermaid's render completes.* Mermaid
always renders a cluster (task container) as `<g class="cluster" id="...">` with exactly two
children: a background `<rect>` and a `<g class="cluster-label">` (the title text) — the label
always sits in its own reserved header strip ABOVE where any leaf node begins (measured on a real
container: label spanned y=310.06→341.4, first leaf node did not start until y=373.7 — a genuine
~32px full-width gap). This band is empty BY CONSTRUCTION regardless of how many/how large a task's
checks are, so it is a reliable click target no matter how content-dense the container is. `diagram.html`'s
embedded script (never `diagram.md`/`Render`/`SemanticContent`/the staleness hash) computes, for
every task container, a full-width band from the cluster's bounding box down to just past the
label's bottom edge (`getBBox()` on both), and appends a real
`<a href="..." target="_blank"><rect fill="transparent"></a>` covering that band as the cluster
group's LAST child. **Appended, not inserted first** — a cluster's only two original children are
its background `<rect>` then its `.cluster-label` group (in that paint order); appending puts the
overlay on top of the background rect (so it is actually clickable) without covering the label text
visually (the rect is transparent either way), while prepending would put the overlay BEHIND the
background rect (which becomes second-in-order and paints over it), silently blocking every click —
this exact mistake was made and caught during implementation. The overlay rect uses
`fill="transparent"` (NOT `fill="none"`) for the same hit-testing reason as the retired anchor node:
real headless-Chrome verification proved an SVG shape with `fill:none` is invisible to hit-testing
(the browser's default `pointer-events:visiblePainted` only treats a shape as clickable once it has
an actual paint), so a `fill:none` overlay would let clicks pass straight through to whatever sat
underneath; a fully transparent (alpha-0) fill paints nothing visually but still counts as painted
for hit-testing.

*Where the task→folder path data comes from.* `MermaidRenderer.TaskFolderTargets(plan)` returns the
task-container-id → plan-relative-folder-path map (the same data the retired anchor's `click href`
used to carry), keyed by the SAME container id (`task_<base>`) the Mermaid source emits. The `graph`
CLI command embeds this as a small JSON object in a
`<script type="application/json" id="task-folder-targets">` element (read back via `textContent`,
the same verbatim/never-interpolated treatment as the Mermaid source itself), and the overlay script
parses it and looks up each `g.cluster`'s target by its own DOM id. The Mermaid source itself
(`Render` and `RenderInteractive` alike) now emits IDENTICAL container/node shape for every task —
no anchor node, no `invisible` classDef, no container `click` directive at all; `RenderInteractive`
differs from `Render` ONLY in the `click` directives it appends for CHECK (leaf) nodes, which are
completely unaffected by any of this — Mermaid already wraps them in a working `<a href>`.

**A task-level preflight still gates its `dependsOn` edge.** A `tasks/<id>/preflights/` check
verifies a producer actually delivered what a consumer depends on; collapsing both into
containers does not erase that relationship. The `task_producer --> task_consumer` edge remains
drawn exactly like any other dependency edge, and the preflight renders as an ordinary check node
directly inside the **consumer's own** container (before the container's guardrail check nodes,
per the emission-order contract above) — it is never re-routed to originate from the preflight
node itself.

**Colouring.** Two `classDef`s colour the leaf check nodes — `preflight` and `guardrail` —
referenced inline (`:::preflight` / `:::guardrail`). The two container kinds (task container,
plan-level container) are coloured per container by a `style <id> …` statement instead, for the
edge-endpoint reason above. There is no `invisible` classDef or anchor-node styling of any kind —
the task-container click target lives entirely in `diagram.html`'s post-render JavaScript overlay
now (see above), never in the Mermaid source.

**Provenance comment.** The first line of `diagram.md` is, verbatim:

```
<!-- guardrails:graph v1 source-sha256=<hash> -->
```

followed by a blank line and a fenced ```` ```mermaid ```` block. The comment carries only
the `source-sha256` identity — no timestamp — so re-running `graph` on an unchanged plan
produces a **byte-identical** file (a deterministic projection, no git churn).

**Caption.** Immediately after the closing mermaid fence, the written `diagram.md` carries a
single italic caption line, verbatim:

```
_Structure only — retry, feedback, and needs-human edges are omitted._
```

The flowchart draws the static task/guardrail/dependency structure only (retry, feedback, and
needs-human edges are out of scope for v1); the caption tells a reader so the diagram is not
mistaken for a one-pass pipeline. The caption lives in the markdown wrapper **only** — NOT
inside the ```` ```mermaid ```` block and NOT in the renderer's `source-sha256` semantic
content — so it does not affect the hash, leaves two regens byte-identical, and is absent from
`--stdout` (which prints the raw diagram, not the document). The legend block (see above)
immediately follows the caption, also outside the hashed content.

**`source-sha256`.** A SHA-256 (lowercase hex) over the diagram's **semantic content**
(container membership, check node labels, and the container→container DAG shape) as emitted by
the renderer, excluding the cosmetic leaf-node `classDef` color definitions and the legend. It
changes whenever the DRAWN diagram changes — a task, a dependency, or a check (container/DAG
shape), or a node label. Since a check's drawn label is always its `name` (issue #222 — never its
`description`, and never truncated), the hash is sensitive to a check's `name` changing but NOT to
a `description`-only edit — a check's description can be freely rewritten (to improve the
click-tooltip text) without moving the hash or making `graph --check` report the plan stale.
**Critically, it folds the PLAN-LEVEL `<plan>/preflights/` and
`<plan>/guardrails/` folder checks too, not just the per-task `tasks{}` structure** — those
checks are not reachable through any task, so a hash computed from task structure alone would
leave the diagram falsely "fresh" after someone edits a Terminal Gate check's label or
adds/removes a Full Flight Check. It is stable across irrelevant input reorderings (the renderer
sorts tasks, checks, and dependents ordinal) and is unaffected by action kind (not drawn), by
styling, or by the legend's wording.

**Command contract.**

- `guardrails graph [folder]` — render and write `diagram.md` + `diagram.html`; print the
  written `diagram.md` path, then (unless `--no-html`) a `Diagram (interactive): <link>` line
  for `diagram.html` — a clickable OSC 8 hyperlink built from the absolute path via .NET's
  `Uri` (reusing `RunCommand.Hyperlink`, the same escape shape `guardrails run`'s `Logs` link
  uses), falling back to the plain absolute path when the terminal cannot render one; exit `0`.
  Building this link in the CLI (issue #249) — rather than the caller hand-assembling a
  `file://` URL from a shell `pwd` — is what keeps it correct under Git Bash/MSYS on Windows,
  whose `pwd` returns the non-resolvable mount form (`/f/...`) instead of the native drive
  form (`F:/...`) a `file://` URI needs. Front-doors through load/validate first: on any
  load/validate error, print diagnostics and exit `1`.
- `--no-html` — write only `diagram.md`; skip `diagram.html`. Has no effect with `--stdout`.
- `--stdout` — print the diagram to stdout; write nothing to disk (neither `diagram.md` nor
  `diagram.html`); exit `0`.
- `--check` — write nothing. Recompute `source-sha256` (including the plan-level folder
  checks — see above), read the value embedded in an existing `diagram.md`, and exit `0` when
  present and equal (fresh). When `diagram.md` is **stale or missing**, print one actionable
  line and exit `2` — the "regenerate" signal. When `diagram.html` is **present but carries a
  different hash**, print one actionable line and exit `2` (a **missing** `diagram.html` is
  NOT stale — the caller may have used `--no-html`). A **load/validate error** front-doors
  first and exits `1`, never reaching the freshness check.
- `--format <mermaid>` — default and only accepted value is `mermaid` (reserved for future
  formats).

---

## 11. Breakdown manifest + regeneration merge (`guardrails.baseline`)

The plan is the **source of truth**. A re-run of `/plan-breakdown` re-derives the task set and
the `dependsOn` DAG from the (changed) plan — these are machine-owned and not hand-edited. The
**only** durable human asset in a generated folder is **guardrail CRUD** (editing a guardrail
script, or adding a new one). So a regeneration must re-derive tasks while **preserving human
guardrail edits**, discarding them only when the task they belong to no longer exists. The
manifest is the deterministic foundation that makes this possible. (Tracked in issue #5.)

### 11.1 The baseline file

`guardrails lock [folder]` captures the **authored** files of a plan folder and writes
`<plan-folder>/guardrails.baseline` — a **committed** artifact (unlike harness-owned `state/`). It
is the BASE that a later regeneration diffs against. The file is named `.baseline` (not `.lock`)
because it is a durable, committed drift-detection reference point; a `.lock` extension would
wrongly imply a gitignored transient mutex (issue #10). The command verb stays `guardrails lock`
— it **writes** the baseline — only the file it produces was renamed.

```jsonc
{
  "version": 1,
  "files": {                              // relativePath (forward-slash, ordinal-sorted) → sha256
    "guardrails.json": "<64-hex>",
    "state/seed.json": "<64-hex>",
    "tasks/01-a/task.json": "<64-hex>",
    "tasks/01-a/guardrails/01-build.ps1": "<64-hex>"
  }
}
```

The baseline carries **no timestamp** — its identity is the `files` map alone, so re-running
`guardrails lock` on an unchanged folder rewrites a **byte-identical** file (a deterministic
projection, no git churn — matching the `diagram.md` precedent in §10).

**Secret-scanner exclusion suggestion (issue #67).** Because the baseline is a committed file of
pure SHA-256 hashes, generic secret scanners (ggshield/GitGuardian) flag a hash as a false-positive
"high entropy secret" and block the commit. The baseline must stay committed (it is the BASE for
merge), so whenever the tool **writes** a baseline — `guardrails lock` and the regeneration
`merge --apply` — it **detects** whether the enclosing git repo's GitGuardian config already
excludes `**/guardrails.baseline` and, when it does not, **prints a copy-pasteable suggestion**. The
tool is **read-only and advisory here: it never modifies, creates, or edits the user's scanner
config** — it only inspects and suggests. The detection prefers `.gitguardian.yaml` over an existing
`.gitguardian.yml` (ggshield precedence) and reads the v2 `secret.ignored-paths` and v1 top-level
`paths-ignore` keys, treating reasonable spellings (`**/guardrails.baseline`, `guardrails.baseline`,
`./guardrails.baseline`) as already-covered so it never nags. The suggestion is **targeted** when a
config exists (naming the file and the exact key for its v1/v2 schema), a **create-this-file** block
when no config exists, and a **generic** line when the config can't be read. It only ever prints, so
it can never affect the exit code, and a read/parse error never escapes into `lock`/`merge` (no
failure coupling). It is a no-op (prints nothing) when there is no enclosing git repo or when the
exclusion is already present.

**Included:** `guardrails.json`, every task's `task.json` / `action.*` / `guardrails/*`, and the
committed `state/seed.json`. **Excluded:** the baseline file itself, the generated `diagram.md`
and `diagram.html`, `*.tmp` (atomic-write residue), and harness-owned runtime under `state/`
(`state.json`, `run.json`, `merge-conflicts.log`, `logs/…`). Hashes are SHA-256 (lowercase hex)
over
**newline-normalized** text (matching `PlanHash`), so CRLF/LF checkouts hash identically.

### 11.2 Drift classification (LOCAL vs BASE)

Comparing a freshly captured snapshot (LOCAL) against the baseline (BASE) classifies each file:

| Status | Meaning |
|---|---|
| `Unchanged` | BASE == LOCAL — human didn't touch it; the merge may take REMOTE freely |
| `Edited` | present in both, content differs — a human edit to preserve |
| `Added` | in LOCAL only — a human-authored file to preserve |
| `Missing` | in BASE only — deleted on disk since the last baseline |

### 11.3 The regeneration merge (BASE / LOCAL / REMOTE)

A re-run has three inputs: **BASE** (the baseline), **LOCAL** (on disk = BASE + human CRUD), and
**REMOTE** (a fresh generation from the changed plan). Per guardrail:

| BASE | LOCAL | REMOTE | result |
|---|---|---|---|
| present | == BASE | changed | take REMOTE (machine owns it) |
| present | edited | == BASE | keep LOCAL (preserve the human edit) |
| present | edited | also changed | **CONFLICT → block the run** until a human applies or discards |
| present | edited | gone (task removed) | drop (task no longer needed → its guardrail goes too) |
| absent | added | absent | keep (human-authored guardrail) |

**Task identity.** Matching across a regeneration uses `stableId` (§3), not the renumbered
folder name, so a "slightly altered + reordered" task carries its human guardrails forward while
a materially changed or removed task does not. **Open question #2 is resolved: the id is a short
*minted* token, not a slug.** `/plan-breakdown` mints one per task on first generation and
*reuses* it for the continuous task on every regeneration (minting only for genuinely new tasks);
folder renames and slug edits therefore don't break identity. The LLM owns this judgment (which
id a regenerated task reuses); `validate` enforces uniqueness (GR2010) and format (GR2011).

**Tasks without a `stableId`** match by folder name (`folder:<name>`) instead. This is a
best-effort fallback, not an equal alternative: the moment a regeneration renumbers or renames
such a task's folder, the merge reads it as *the old task dropped + a new task added*, so any human
guardrail edits on it are lost (the drop is surfaced as a warning, never silent). The merge emits a
one-line heads-up whenever either side has folder-fallback tasks. Pre-`stableId` folders therefore
sit on this boundary until re-minted; `/plan-breakdown` mints an id per task so new work doesn't.

**Per-task file matching.** Within a matched task, the merge resolves **every file under the
task's `guardrails/` directory** by its full filename (not the guardrail's logical name): the
script, its `*.prompt.md`, its metadata sidecar (`<basename>.json`, §4.1), and any file a human
added there. All are human-ownable content, so all flow through the same per-file resolution — a
human-tuned `timeoutSeconds` in a sidecar is preserved exactly like an edited script body.

**Guardrail-granularity refinements.** The five-row table is the conceptual contract; at the
per-file level two more cases resolve to **CONFLICT** because human work would otherwise be lost
silently: (a) a human *edited* a guardrail that the regeneration *removed* from a surviving task,
and (b) a human *added* a guardrail whose filename the regeneration also produced, with different
content. A human-added guardrail the regeneration doesn't emit is simply kept. A guardrail the
human *deleted* that the regeneration re-emits is taken from REMOTE (the plan wins) but reported as
a **reinstated** warning — the deletion is being undone, not honored.

**What's machine-owned.** Only files under `guardrails/` are preserved. Everything else in a task —
its `task.json`, `action.*`, and the `dependsOn` DAG — plus `guardrails.json` is re-derived from
the plan (taken from REMOTE). `state/seed.json` is treated leniently: adopted from REMOTE when
present, otherwise left as-is. A human edit to one of these machine-owned files is overwritten by a
differing REMOTE — that is contractual, but never silent: the merge warns (and `lock --diff` would
have shown the file as `EDITED`), so the human can move the change into the plan if it mattered.

The deterministic engine (`BreakdownMerge`) and the `guardrails merge` command (§11.5) implement
all of the above; the `/plan-breakdown` skill orchestrates them (§11.5).

### 11.4 Command contract

Exit codes follow §7: `0` clean, `1` a genuine error, `2` an actionable "regenerate" condition
(the same signal `graph --check` uses for a stale/missing diagram).

- `guardrails lock [folder]` — capture authored files and write `guardrails.baseline`; print the
  path + file count; exit `0`. A pure content snapshot — it does **not** load or validate the
  plan (run `guardrails validate` for that). Missing folder → exit `1`. (The verb stays `lock` —
  it WRITES the baseline; only the produced file was renamed from `guardrails.lock`, issue #10.)
- `--check` — write nothing. Recompute the snapshot and compare to the baseline: clean → exit `0`;
  drift **or a missing baseline** → one actionable line and exit `2` (the "regenerate" signal,
  distinct from a genuine error so CI can tell "re-run `guardrails lock`" apart from "the tool
  failed"). A **corrupt** baseline (present but unparseable) → exit `1`.
- `--diff` — write nothing. Print one line per changed file (`EDITED` / `ADDED` / `MISSING`)
  and exit `0` (printing the report IS the success, drift or not). A **missing** baseline → exit
  `2` (run `guardrails lock` first — there is no BASE to diff against); a **corrupt** baseline →
  exit `1`.

### 11.5 The `merge` command + skill orchestration

`guardrails merge [folder] --remote <dir> [--apply]` runs the regeneration merge (§11.3).
`folder` is the current plan folder (LOCAL, carrying `guardrails.baseline` = BASE); `--remote` is a
freshly generated candidate (REMOTE) staged from the changed plan. Both sides are loaded +
validated (so a duplicate `stableId` surfaces as GR2010 here too).

- default (**dry run**) — compute and print the resolutions (`CONFLICT` / `KEEP` / `DROP` lines,
  warnings, and a summary; `TakeRemote` is summarized as a count). Writes nothing. Exit `0` when
  there are no conflicts, `2` when there are.
- `--apply` — when there are no conflicts, materialize the merge **in place**: replace the
  authored content (`tasks/`, `guardrails.json`, and `state/seed.json` when REMOTE has one) with
  REMOTE's, overlay the preserved human guardrails onto the REMOTE task structure, and re-write the
  baseline so the merged folder is the new BASE. Harness-owned `state/` runtime and the generated
  `diagram.md` are left untouched. With conflicts present, `--apply` changes nothing and exits `2`.
  The new `tasks/` tree is assembled in a sibling staging directory and swapped in only once
  complete, so a failure mid-apply leaves the existing folder intact rather than half-written with a
  stale baseline. On success it prints the re-written baseline path and a reminder to run `validate`
  then `graph` (the merge deliberately leaves the old diagram stale, and does **not** need a second
  `lock` — `--apply` already wrote the baseline).
- exit codes (§7): `0` clean (dry run with no conflicts, or applied); `2` the actionable "a human
  must act" signal — unresolved conflicts, or a **missing** baseline (run `guardrails lock` first to
  adopt the current folder as BASE); `1` a genuine error (missing folder/remote, **corrupt**
  baseline — present but unparseable, distinct from a missing one — or an invalid plan on either
  side). The missing-baseline (`2`) vs corrupt-baseline (`1`) split mirrors `lock --check`/`--diff`
  (§11.4).

**Conflict presentation (open question #3 resolved): block + report.** Conflicts are printed to
stdout — one `CONFLICT <stableId>/<file> — <reason>` line each — and the run is blocked (exit `2`);
no `--apply` proceeds until none remain. The human resolves by editing the guardrail (or the plan)
and re-running. (`.orig`-style inline markers are a possible future addition; the run-blocking
*policy* is what's contractual.)

**Skill flow (`/plan-breakdown`, regeneration path).** When the folder already exists and the
user chooses *merge*: (1) generate the new breakdown into a **staging** folder, reusing each
continuous task's `stableId` from the existing `task.json` and minting ids only for new tasks;
(2) `guardrails merge <folder> --remote <staging>` (dry run) — on exit `2`, surface the conflicts
and **stop**; (3) on exit `0`, `guardrails merge <folder> --remote <staging> --apply`, then
`guardrails validate` + `guardrails graph`. The skill never hand-applies the per-guardrail
decisions — the deterministic engine owns them.

---

## 12. Log viewer (`run` live links + `guardrails logs`)

The **canonical "all tasks" page is the static index file** `logs/<runId>/index.html` (§12.3) — a
`file://` artifact that is **durable** (it works after the harness stops) and has **no server
dependency**. A small **loopback-only** HTTP server is the **transient tailing backend** for
**active** tasks: it surfaces each task's per-attempt log artifacts (§8) live while a run is in
flight, so a human can answer "is it actually working?" without leaving the terminal. The static
index links a *running* task to this server; the user clicks through, tails the live page, and hits
the browser **Back** button to return. The server serves the same on-disk files documented in §8; it
adds no new artifacts and is never part of the plan contract (the loader/validator ignore it
entirely). The task page also surfaces a **Source** section — the task's action file and
`guardrails/*` scripts (derived from the plan's `TaskNode`, not from `logs/`) — so a thrown
guardrail's script is one click from its failing log (issue #141 item 3).

**Static is the durable site; live is an active-only leaf (issue #143).** Because the live server
dies when the harness stops, it is deliberately **not** part of the durable navigable site:

- `GET /` is **not** an all-tasks landing. It is a small **pointer note** naming the canonical static
  index file by its absolute path (a browser blocks `http://` → `file://`, so the path is shown as
  **text** to open, not linked). The server cannot — and does not — render a second, harness-dependent
  task table.
- The live per-task page is an active-task **deadend**: it carries **no** "all tasks" navigation. The
  user arrives by clicking a running task on the static index and leaves via the browser Back button.

Rationale: the static pages are durable and server-independent; the live page is inherently transient,
so it is an active-task leaf, not part of the durable navigable site. The journal-projected **Status**
table lives on the static index (§12.3), which is the single all-tasks surface.

**Binding and safety.** The server binds to the numeric loopback address `127.0.0.1` on a port (an
automatically chosen free ephemeral port by default), **never** to a routable interface — logs may
echo secrets, so they are never exposed off the local machine (the numeric bind is deliberate, so a
custom `/etc/hosts` mapping of `localhost` cannot widen the exposure). Responses carry
`X-Content-Type-Options: nosniff` and `X-Frame-Options: DENY`. The log-file surface is confined to
`logs/<runId>/<task-id>/` (SSOT §8): the run is selected by the journal's `runId` (§7), the requested
task id must be one the plan declares, and the requested filename must be a bare name inside the
selected `attempt-N/` directory (no traversal). The **source** surface (`/source`, `/sourcefile`) is
confined a different way — to the task's *known* source set (action + guardrails + sidecars,
precomputed from the `TaskNode`): a requested `name` is resolved through that set, and the served path
is the known absolute source path, **never** built from the request — so an unknown / traversal name
simply has no entry and is rejected (path-safe by construction).

**Attempt selection.** Both `files` and `file` take an optional `attempt=N` query: the selected
attempt is that `attempt-N/` directory when it exists, else the latest attempt (an unknown/absent N
falls back to latest rather than 404, so a mid-run page stays usable when a URL names an attempt
that has not started). The task page renders an **attempt selector** beside the file selector — the
live viewer can inspect a finished `attempt-1` while `attempt-2` runs.

**Routes** (both the live and post-mortem servers expose the same set):

| Route | Serves |
|---|---|
| `GET /` | a **pointer note** (issue #143) naming the canonical static index file `logs/<runId>/index.html` by its absolute path (shown as text — a browser blocks `http://` → `file://`); **not** an all-tasks table |
| `GET /tasks/{id}` | a page that tails an attempt's log directory for task `{id}` (latest by default; an attempt selector navigates to any prior attempt), plus a **Source** section (issue #141 item 3). An active-task **deadend** — no "all tasks" link (issue #143); the user reaches it from the static index and returns via Back |
| `GET /tasks/{id}/files[?attempt=N]` | JSON `{ attempt, attempts[], preferred, files[], fileDetails[] }` — the SELECTED attempt number (default = latest), every available attempt number ascending, a preferred file to open first (`transcript.md`, else `claude-stream.jsonl`, else `action-stdout.log`, else the first file), the selected attempt's filenames, and a `fileDetails[]` of `{ name, size, empty }` per file (so a zero-byte capture is greyed + "(empty)" in the file dropdown — issue #141 item 4) |
| `GET /tasks/{id}/file?name={f}[&attempt=N]` | the raw text of one log file from the selected attempt (default = latest; read with a shared handle so an in-flight writer is not blocked) |
| `GET /tasks/{id}/source` | JSON `{ sources[] }` of `{ name, label, empty }` — the task's action file + each guardrail script and `.json` sidecar (issue #141 item 3), derived from the plan's `TaskNode` (`action.path` + `guardrails/*`), so a thrown guardrail's script is one click from its log |
| `GET /tasks/{id}/sourcefile?name={f}` | the raw text of ONE of the task's known source files. `{name}` is resolved **only** against the precomputed source set (action + guardrails + sidecars); an unknown / traversal name has no entry and is rejected — the served path is the known absolute source path, never derived from the request, so the surface is inherently confined to the declared sources |

### 12.1 `guardrails run` — live log links

`run` starts the server as the **active-task tailing backend** companion to the live progress table.
The **prominent** "all tasks" line the run prints is the clickable `file://` link to the canonical
**static index** (below); the live server's base URL is printed de-emphasised as the *live tailing
server (active tasks)* — the user navigates from the static index, which links a running task to it
(issue #143). The live progress table still carries clickable per-task "view log" links for running
tasks (to `http://…/tasks/{id}`). The server is started **only** on the interactive path (a live UI,
output not redirected) — nobody clicks links in CI or piped output — and a bind failure is
**non-fatal**: the run prints one warning and proceeds without links. The server's lifetime is the
run; it is disposed when the run ends.

**On-the-fly static site (issue #141 item 2).** Independently of the server, `run` also keeps the
**static** log site (§12.3) up to date as the run proceeds — on **both** the live and the `--no-ui`
paths, since a `file://` "all tasks" page is useful headless too. A decorator `IRunObserver`
(`OnTheFlyLogSiteObserver`) wraps the real observer, and after each forwarded event rewrites
`logs/<runId>/index.html` via the same `LogSiteRenderer`: at run start an all-pending index; on a
task **starting** it flips to `running` and (when the live server is up) links to the live URL; on a
task **finishing** it writes that task's static page and the index links to it. The during-run index
carries a `meta refresh` so a `file://` view picks up the rewrites. At run **end**, the durable final
site is written (`ExportSite` — all-static links, **no** refresh, every task page), so the artifact
left on disk is complete and self-contained — identical to `logs --export`. The run prints a
clickable `file://` link to this static "all tasks" index at **start and end**, alongside the live
URL. A finished task's terminal `logs` link (the live table's post-mortem link) targets that task's
**static page** `logs/<runId>/<task-id>/index.html` — a rendered HTML page — not the log directory
(issue #141 item 1). Site writes are best-effort: a render hiccup never changes the run's exit code.

| Flag | Default | Meaning |
|---|---|---|
| `--no-log-server` | off (server on) | Do not start the log server / per-task links (headless or CI use). The server is also skipped whenever the run is non-interactive or `--no-ui` is set, regardless of this flag. |
| `--log-port <n>` | `0` | Port for the live log server. `0` = an automatically chosen free port. Bound to localhost only. |

### 12.2 `guardrails logs` — post-mortem viewer

`guardrails logs [folder] [--port n] [--task id] [--no-open]` reviews a plan's **persisted** logs,
decoupled from any active run — the post-mortem companion for reviewing an overnight run, or judging
whether a *passing* task's guardrails were strong enough, from the same attempt logs. It
(re)generates the **static** site for the journal-selected run and advertises the canonical static
index file (`logs/<runId>/index.html`) as the **entry point** by its `file://` path (issue #143), and
also starts the live tailing server (so a *running* task's live page works — for a completed run the
server simply goes unused). With `--no-open` it opens nothing; otherwise it opens the static index
(or, with `--task`, the named running task's live page). It runs until Ctrl-C, then exits `0`. The
folder argument defaults to the current directory and follows the §7 plan-file → task-folder fixup.

The journal-projected coloured **Status** column (`succeeded` / `running` / `needs-human` / `blocked`
/ `failed` / `pending`) lives on that static index (§12.3), which is the single all-tasks surface —
the live server no longer renders one (issue #143).

| Flag | Default | Meaning |
|---|---|---|
| `--port <n>` | `0` | Port for the live tailing server. `0` = an automatically chosen free port. Bound to localhost only. |
| `--task <id>` | (none) | Open straight to this task's live tailing page instead of the static index. An unknown id falls back to the static index with a notice. |
| `--no-open` | off | Do not launch a browser; just print the static index path + server URL (headless hosts). |

**Exit codes.** `0` on a clean serve or clean shutdown (Ctrl-C). A load/validate failure prints
diagnostics and exits `1`. When the plan has **no run journal yet** (never run), `logs` prints a
one-line notice and exits `0` — there is nothing to post-mortem, which is not an error. A bind
failure exits `1`.

### 12.3 Durable static export (`guardrails logs --export`)

The same **self-contained static HTML site** is produced two ways: **during a run** (written on the
fly as tasks settle — §12.1) and **post-hoc** by `guardrails logs [folder] --export`, which renders
the journal-selected run's logs and exits `0` without starting the server or blocking. Either way the
site is written **next to the artifacts it renders**, under the `logs/` audit tree (never `state/`,
which holds mutable run state):
- `logs/<runId>/<task-id>/index.html` — one page per task that has attempts on disk, inlining that
  task's per-attempt artifacts (§8). **When a task has more than one attempt** (#206), an attempt
  `<select>` — mirroring the live viewer's attempt selector (§12.1) — sits above the attempts and
  shows/hides each attempt's `<section data-attempt="N">`, defaulting to the **latest** attempt (the
  live viewer's default); every attempt's markup stays inlined in the one exported file (single-file
  portability — a `file://` page can't route by `?attempt=N` the way the live server does), the
  dropdown only toggles which `<section>` is visible. A task with a **single** attempt renders **no**
  attempt dropdown — its one section is simply always visible (the common case; nothing to pick
  between). Nested inside each attempt's section, unchanged, is that attempt's file `<select>`
  **combobox** that toggles between that attempt's files, **all inlined** as hidden `<pre>` blocks (the
  preferred file — `transcript.md`, else `claude-stream.jsonl`, else `action-stdout.log` — shown
  first). A `file://` page can't fetch siblings, so every file's content is baked in and shown/hidden
  by a tiny vanilla-JS DOM toggle (**no fetch** — works offline on `file://`), replacing the old
  `·`-separated link row (#145 Feature 2); the attempt-level toggle (#206) reuses this SAME
  querySelectorAll/`hidden`-flag mechanism, scoped by `data-attempt`, rather than a second pattern. A
  zero-byte file renders "no output captured" and its option is greyed + "(empty)" (#141 item 4).
  Inlining every attempt's every file bloats the page by the full raw-stream size — accepted (uncapped)
  for the audit/demo use, since `file://` has no other way to show siblings. A **Source** section
  follows the attempts: relative `file://` links back to the action file and every `guardrails/*`
  script + `.json` sidecar (#141 item 3), the static twin of the live page's Source list.
- `logs/<runId>/index.html` — the site index, a **projection of the journal** (§7) regenerated on
  every write (never appended): every task with its status word; a task with attempts on disk is a
  **link** to its page, a not-yet-run task is **plain text** (the #103 linkability rule). The
  **during-run** index additionally carries a `meta refresh` and links a *running* task to the live
  server; the **final / `--export`** index has **no** refresh and **all-static** links (durable,
  non-flickering).

Pages are produced by the **same renderer** the live/post-mortem server uses (`LogSiteRenderer`,
which owns the shared page shell — CSS, layout, status colours — that the live `LogServer` templates
also embed) — there is **no forked static look-alike** (#103 Request 2). Each write is re-runnable and
idempotent (regenerates the whole site each call, like `guardrails graph`); the during-run writer and
`--export` produce the same durable bytes at run end. It is **non-authored audit** (excluded from
`guardrails.baseline`, like `diagram.html`, because it lives under `logs/`) and is cleared with the
rest of `logs/` by `--fresh` (§6.1). `--port`/`--task` are serve-mode options and are ignored with
`--export`. A missing/in-flight attempt artifact renders as "no output captured" — a static snapshot
of an in-flight run is valid and never errors.

---

## 13. Review marker (`state/guardrails-review.json`)

`/guardrails-review` records that a human ran the adversarial review pass over the current plan, by
invoking **`guardrails mark-reviewed <folder>`** (the writer — issue #131; the skill can't compute the
`PlanHash` itself, so it delegates to the CLI) which writes a **committed** marker under `state/`:

```jsonc
{
  "version": 1,
  "reviewedAt": "2026-06-22T14:03:11Z",   // ISO-8601 UTC, review time
  "planHash": "sha256:…"                   // PlanHash (§7) computed at review time
}
```

The `planHash` is the **same `PlanHash`** the journal records (§7) — `guardrails.json` + every
`task.json`, newline-normalized, task-id-ordered. **Staleness** is a deterministic compare: marker
absent ⇒ *missing*; `planHash` ≠ the plan's current `PlanHash` ⇒ *stale* (the plan's task structure
changed since review); equal ⇒ *reviewed*. A present-but-unparseable marker is treated as *missing*
(never throws). `PlanHash` covers task structure, not guardrail-script bodies — a guardrail-script
edit does not by itself re-flag a review; this is intentional under the "plan changed" framing.

The marker is **committed as part of the reviewed plan**, alongside the committed task folder and the
review's edits. It is an attestation about the **committed plan content** — not about a particular
checkout — and because it is `planHash`-keyed it **self-invalidates the instant any `task.json` or
`guardrails.json` changes the `PlanHash`** (the GR2025 nudge returns), so a committed marker can never
falsely vouch for changed content. That self-invalidation is exactly what makes committing it safe and
correct: it travels with the plan it attests to, and any edit that the `PlanHash` covers reads as
un-reviewed rather than as a false green. It is therefore **NOT wiped by `--fresh`** (§6.1) — `--fresh`
clears genuine per-run runtime state (`run.json`, `state.json`, `merge-conflicts.log`, `logs/`,
`captured/`), not committed plan artifacts. (As above, `PlanHash` covers task structure + config, not
guardrail-script bodies, so a script-body-only edit does not by itself re-flag the review.)

**Surfacing (warn, never block — issue #79):**
- `guardrails validate` appends **GR2025 (warning)** when the marker is missing or stale, naming the
  reviewed-vs-current short hash. A warning never fails `validate`'s exit code. The nudge is a
  **command-layer** concern (`PlanValidator.ReviewMarkerDiagnostic`), deliberately NOT part of the
  pure semantic `PlanValidator.Validate` set, so a plan that lacks a marker is not flagged by the
  harness's own internal validation.
- `guardrails run` (and `--dry-run`) print the same nudge before launching, suppressible with
  `--skip-review-check`.

The marker is **written by the `/guardrails-review` skill**; the harness only reads it
(`ReviewMarker.Read`/`Evaluate`), computes staleness, and surfaces the warning.
