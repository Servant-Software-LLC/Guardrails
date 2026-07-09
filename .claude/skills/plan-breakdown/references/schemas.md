# Schema quick-reference for generated plan folders

**This is an excerpt.** The single source of truth is
`docs/plans/02-schemas-and-contracts.md` in the Guardrails repo
(https://github.com/Servant-Software-LLC/Guardrails). If this file and the SSOT
disagree, the SSOT wins — and this file is due an update.

## Folder shape (generated next to `<plan-name>.md`)

```
plan-name/
├── guardrails.json
├── state/seed.json              # optional committed initial state
├── preflights/                  # OPTIONAL plan-level "Full Flight Checks" — run ONCE, before the DAG (§3.3/§4)
│   ├── 01-<precondition>.ps1     #   guardrail-shaped file (same parser as tasks/<id>/guardrails/)
│   └── 01-<precondition>.json    #   optional metadata sidecar
├── guardrails/                  # OPTIONAL plan-level "Terminal Gate" — run ONCE, at run end, on the merged HEAD (§3.3/§4)
│   ├── 01-<integration-check>.ps1  #   ≥1 real integration-set re-run for a multi-leaf/fan-in plan (GR2028)
│   └── 01-<integration-check>.json #   optional sidecar (e.g. `"scope": "integration"`)
└── tasks/<NN-verb-object>/
    ├── task.json
    ├── action.prompt.md | action.ps1 | action.sh | …   # exactly ONE action.* file
    ├── preflights/              # OPTIONAL task-level JIT dependency-delivery check — run at taskBase, before the action
    │   └── 01-<dep-delivered>.ps1
    └── guardrails/              # ≥1 guardrail or validation FAILS
        ├── 01-<what-it-proves>.ps1
        ├── 01-<what-it-proves>.json    # optional sidecar: description/args/timeoutSeconds
        └── 02-<what-it-proves>.prompt.md
```

Never generate `state/state.json`, `state/run.json`, or `state/logs/` — those are
harness-owned runtime artifacts (and gitignored).

**Two scopes, four folders (SSOT §1/§3.3).** `preflights/` and `guardrails/` are first-class
folders at TWO scopes. **Plan-level** `<plan>/preflights/` (the "Full Flight Checks") runs ONCE
before the DAG against the starting repo; **plan-level** `<plan>/guardrails/` (the "Terminal Gate")
runs ONCE at run end on the merged plan-branch HEAD — it REPLACES the retired `integrationGate: true`
sink-task (see `task.json` below). **Task-level** `tasks/<id>/preflights/` is a per-task JIT
dependency-delivery check run in the task's segment worktree before its action, the sibling of the
postcondition `tasks/<id>/guardrails/`. All four folders share **one** guardrail-file parser (same
`NN-name.ps1`/`.sh`/`.py` + optional `.json` sidecar, or `NN-name.prompt.md`; ordinal sort); every
file MUST open with a `catches:` declaration and a malformed one (no `catches:`) is a hard load error,
**GR2027**. See SKILL.md Step 4 (four-folder doctrine) and Step 5 (the `<plan>/preflights/` baseline).

`diagram.md` at the plan-folder root is also **generated, not hand-authored** —
emitted by `guardrails graph`, carrying a `<!-- guardrails:graph … source-sha256=… -->`
provenance comment whose hash is the staleness key. It is not plan input and must not
be hand-edited; the loader/validator ignore it. See SSOT §10 (Diagram artifact).

## Waved plans — nested layout (SSOT §14)

A **waved** plan (a plan of ordered STAGES, each building on the prior stage's materialized output —
SKILL.md Step 0.8 / Step 9) replaces the plan-root `tasks/` with ordered **wave subfolders**, each a
self-contained mini-plan. This is an excerpt of SSOT §14 — if they disagree, §14 wins.

```
plan-name/
├── guardrails.json                 # ONE shared run config for the whole plan (no per-wave config in v1)
├── state/seed.json                 # optional; ONE continuous state/journal for the whole run
├── preflights/  guardrails/        # OPTIONAL plan-root Full Flight Checks / Terminal Gate (additive)
├── diagram.md                      # OPTIONAL plan-level wave map (generated)
└── wave-01-<slug>/                 # a wave = a mini-plan folder
    ├── preflights/                 #   wave ENTRY gate  ("the prior wave's outputs materialized")
    ├── guardrails/                 #   wave EXIT gate   ("this wave's terminal postconditions")
    └── tasks/<NN-verb-object>/…    #   the wave's own task DAG (same shape as a flat plan's tasks/)
    wave-02-<slug>/ …
```

- **Detection:** a plan is *waved* iff it has **no root `tasks/`** AND ≥1 immediate subdir matching
  **`^wave-([0-9]+)-[a-z0-9-]+$`**. The numeric `NN` is **load-bearing** — it drives the strict total
  order (there is no `dependsOnWave` edge). Number contiguously from `01`.
- **Validation codes (SSOT §14.1):** **GR2032** mixed layout (both a root `tasks/` AND wave dirs);
  **GR2033** wave numbering (duplicate `NN`, or a non-conforming sibling dir next to wave dirs = error;
  a numbering **gap** = warning); **GR2034** a cross-wave `dependsOn` edge (`dependsOn` is intra-wave,
  plain sibling folder names only — cross-wave ordering is the barrier's job).
- **Wave-qualified identity (SSOT §14.2):** a task's canonical id is **`<waveDir>/<taskFolder>`** (e.g.
  `wave-02-provision/01-author-tests`). This is the journal key, the resume trailer value, AND the
  **§6.2 single-writer state-fragment key** — a waved-plan prompt action's fragment MUST be keyed by
  the wave-qualified id: `{ "wave-02-provision/01-author-tests": { … } }` (a bare `01-author-tests` key
  is rejected as foreign every attempt). Two waves may reuse `01-` numbering with zero collision.
- **The wave four-folder scope (SSOT §14.3):** `<plan>/<wave>/preflights/` = the wave ENTRY gate
  (the prior wave materialized — the #181 positive-baseline archetype at the boundary);
  `<plan>/<wave>/guardrails/` = the wave EXIT gate (this wave's terminal postconditions; **GR2028
  per wave** — a multi-leaf/fan-in wave needs ≥1 real integration re-run). The **last wave's exit gate
  runs on the fully-merged HEAD** and is the whole-plan terminal soundness boundary, so a plan-root
  `<plan>/guardrails/` is optional-additive. All folders share the ONE guardrail-file parser (a
  malformed file with no `catches:` is GR2027).

## `guardrails.json` — minimum to emit

```jsonc
{
  "version": 1,                  // REQUIRED
  "maxParallelism": 4,
  "defaultRetries": 2,
  "defaultTimeoutSeconds": 1800,
  "guardrailMode": "failFast",   // keep failFast; order guardrails cheapest-first
  "workspace": ".."              // the repo the plan operates ON, relative to plan dir
}
```

**Mind the workspace depth.** `".."` is right only when the plan folder sits directly
in the workspace root. A plan nested deeper points further up — e.g. a plan at
`docs/plans/my-plan/` operating on the repo root needs `"workspace": "../../.."`.
Compute it from where the folder actually lives; `guardrails plan` + a dry-run of the
first guardrail are cheap sanity checks.

**If ANY task or guardrail is a `.prompt.md`, a `promptRunners` block is REQUIRED**
(validation error GR2008 otherwise), with a resolvable default:

<!-- canonical-schema:promptRunners — MIRRORS docs/plans/02-schemas-and-contracts.md §2 verbatim.
     Keep byte-identical to the SSOT's `"promptRunners": { … }` block (drift-tested). Edit the
     SSOT first, then copy here — never diverge. The leading indent is the SSOT's (it is nested in
     the full guardrails.json example there); preserved so the two regions compare byte-for-byte. -->
```jsonc
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
```
<!-- /canonical-schema:promptRunners -->

Scope `allowedTools` to what the plan's actions genuinely need — `Bash(dotnet *)` for
a .NET plan, not blanket `Bash`.

**Multi-task plans (≥2 tasks joined by `dependsOn`) should default-include read-only git
inspection (#252).** Add `Bash(git log*)`, `Bash(git diff*)`, `Bash(git show*)`,
`Bash(git status*)` to the default alongside the stack-specific entries, so a downstream
task's action prompt can inspect what an ancestor task already committed instead of
burning turns on rejected `git log`/`git diff` attempts and falling back to broad
`Grep`/`Glob` sweeps. Never add **state-mutating** git commands (`restore`, `reset`,
`checkout`, `push`, `commit`, `stash`) to this default — those stay outside
`allowedTools`. A single-task plan has nothing yet to inspect — omit the git entries
there.

## `tasks/<id>/task.json`

```jsonc
{
  "description": "One line — feeds retry feedback and the UI",   // REQUIRED
  "stableId": "k3f9a1",     // OPTIONAL — short minted token, UNIQUE within the plan — the
                            // regeneration merge's identity key (§11). Mint once; reuse for the
                            // same task across regenerations; never reuse for a different task.
                            // absent ⇒ identity falls back to the folder name (§3);
                            // duplicate ⇒ GR2010; must match ^[a-z0-9][a-z0-9._-]*$ ⇒ GR2011.
  "dependsOn": ["01-other-task"],                                // REQUIRED (may be [])
  // NOTE: "integrationGate": true is RETIRED — the terminal gate is now the <plan>/guardrails/
  //       folder (§3.3), NOT a task kind. Still declaring it is a HARD validation error (GR2029).
  //       Do NOT add this key to a new task.json.
  "writeScope": ["src/Foo/"], // optional; workspace-relative path prefixes/globs the task may
                            // add/modify/delete/rename — a deterministic READ-ONLY check (§3.4).
                            // ABSENT ⇒ NO check. Renames = paired D+A (both in scope); the check
                            // NEVER reverts. A vacuous "**"/bare top-level dir is a granularity
                            // smell. Escapes the workspace ⇒ GR2019; vacuous/over-broad ⇒ GR2020.
  "retries": 3,             // optional; overrides defaultRetries
  "timeoutSeconds": 3600    // optional
}
```

Task ids = folder names, `NN-verb-object` kebab-case; NN is a topological hint for
human scanning, `dependsOn` is the truth. `stableId` is OPTIONAL in the schema but the breakdown
**mints one per task by default** — it is the identity that survives a renumber/rename (the
regeneration merge's key, §11). Absent ⇒ identity falls back to the folder name; duplicate within
the plan ⇒ GR2010; a value not matching `^[a-z0-9][a-z0-9._-]*$` ⇒ GR2011. Omit the `action` block
when the task folder has exactly one `action.*` file (the convention); zero or multiple =
validation error.

**The terminal integration gate is a FOLDER, not a task (SSOT §3.3).** The `integrationGate: true`
task kind is **retired** — there is no terminal-sink task. The terminal whole-repo integration gate
now lives in the plan-root **`<plan>/guardrails/`** folder (a sibling of `tasks/`, evaluated once at
run end on the fully merged plan-branch HEAD; it re-runs the integration set — typically the whole-repo
build and full suite — as the whole-repo soundness boundary). A plan that STILL declares
`integrationGate: true` is a **hard validation error (GR2029)** — there is no coexistence window.
**GR2017 is gone.** **GR2018's content teeth are re-homed onto the folder as GR2028:** a plan with ≥2
leaf tasks or any fan-in — in worktree mode (`maxParallelism > 1`) — MUST have a `<plan>/guardrails/`
folder carrying **≥1 deterministic check that ACTUALLY re-runs the integration set** (a whole-repo
build / full suite / a union invariant, NOT a tautological `exit 0`). A single linear chain (one leaf,
no fan-in) forms no union and is exempt. `scope: "integration"` is **unchanged** — it stays the §4.3
per-union tag (see the guardrail sidecar, below). See SKILL.md Step 4 and `example-breakdown.md` for
the worked terminal folder.

`writeScope` (optional) is a list of workspace-relative path prefixes/globs declaring the surface a
task may add/modify/delete/rename. It drives a **deterministic, read-only** harness check (SSOT
§3.4): after the action and before the task's own guardrails, the harness diffs the task's segment
worktree and asserts every changed path is in scope — a violation retries with feedback naming the
out-of-scope paths (eventual `needs-human`). The check **never reverts**. **Renames** are a paired
**D + A** (both paths must be in scope); **deletions** — the deleted path must be in scope. **Absent
⇒ no check** (the off-switch): a task you cannot confidently scope omits the field and is reported as
a broad surface — **never** give it a vacuous `**`. A scope that escapes the workspace ⇒ **GR2019**
(error); a vacuous/over-broad scope ⇒ **GR2020** (warning). **TDD test-protection:** the test-author
task owns its test files in `writeScope`; the implementation task's `writeScope` EXCLUDES them, so
the check deterministically enforces "the implementation may not write the tests" — the replacement
for the removed `captureHashes`/`tests-untouched`/`restoreOnRetry` triad. See SKILL.md Step 5.

## Prompt files (`.prompt.md`)

Optional YAML frontmatter: `description`, `runner`, `maxTurns`, `timeoutSeconds`.
Precedence for prompt actions: `task.json action.*` → frontmatter → runner config.

<!-- BEGIN ADDED SECTION #94 — per-task maxTurns budgeting (cites SSOT §2/§3; auto-merge friendly) -->
### Per-task `maxTurns` (prompt actions only) (#94)

`maxTurns` caps the agent's turn budget for a prompt action. It resolves in three places, highest
precedence first (SSOT §2/§3):

| Where | Field | Scope | Notes |
|---|---|---|---|
| `task.json` | `action.maxTurns` | one task | prompt actions only; overrides the two below |
| `.prompt.md` frontmatter | `maxTurns:` | one prompt file | overrides the runner config below |
| `guardrails.json` | `promptRunners.<name>.maxTurns` (default **50**) | every prompt task | the flat fallback; `guardrailOverrides.maxTurns` (default 20) for verdict-only guardrail prompts |

The default 50 is right for most tasks. The breakdown raises **only** the turn-expensive archetypes
(integration/smoke/e2e + in-process harness, unfamiliar-SDK discovery, terminal aggregation/wiring)
to a fixed **75** via the per-task `action.maxTurns` (or frontmatter) override — see SKILL.md Step 4a
and the catalogue's "maxTurns budgeting (#94)" section. Script tasks have no `maxTurns`. A guessed
exact budget is unguessable; the fixed bump is a first-attempt cushion, paired with a harness-side
auto-escalate-on-`max_turns` retry policy (a separate harness concern, not emitted by the breakdown).
<!-- END ADDED SECTION #94 -->


The harness automatically appends to every composed prompt: the shared state (inline
or by path), the fragment output contract + `needsHuman` escape (actions), prior
attempt feedback (actions, attempt ≥ 2), and the full verdict contract (guardrails).
**Authored prompts should NOT restate those mechanics** — but every prompt ACTION
must still carry the harness-contract header block (see SKILL.md Step 6) because the
human reviewing the folder needs the contract visible, and the instructions must
survive if a human runs the prompt outside the harness.

## Contracts cheat-sheet

- Deterministic guardrail: exit 0 = pass; on failure print ONE actionable line to stdout.
- Prompt guardrail: write `{ "pass": bool, "reason": string }` to `GUARDRAILS_VERDICT_OUT`.
- Action state: read `GUARDRAILS_STATE_IN` (snapshot), write a JSON object fragment to
  `GUARDRAILS_STATE_OUT`. **Single-writer-per-key is ENFORCED (SSOT §6.2):** a fragment's
  top-level keys must each be the task's **own id = its FOLDER NAME** — the directory the
  `task.json` lives in, e.g. `{ "02-generate-greeting": { … } }` (reserved keys — none in v1).
  **The key is NOT the `stableId`** (an internal regeneration token, §3); a `stableId`-shaped
  top-level key is a foreign/unowned key. A foreign task id or any arbitrary shared key makes the
  fragment invalid — it is rejected (not stripped), the attempt fails, and nothing merges (every
  retry). Invalid (non-object/unparseable) fragment = attempt fails too.
- Retry: attempt ≥ 2 receives `GUARDRAILS_FEEDBACK` (path to feedback.md).
- Guardrails see `GUARDRAILS_ACTION_STDOUT/_STDERR/_RESULT` and `GUARDRAILS_STATE_FRAGMENT`.
- cwd of every child process = `workspace`. Plan-folder paths arrive absolute via env vars.
- Harness exit codes: 0 green · 1 error · 2 needs-human · 3 cancelled.

## Regeneration merge (§11.5)

`guardrails merge <folder> --remote <dir> [--apply]` runs the identity-aware regeneration merge
(SSOT §11.3/§11.5). `<folder>` is LOCAL (carries `guardrails.baseline` = BASE); `--remote` is the
freshly staged candidate from the changed plan.

- **Dry-run exit codes:** `0` = no conflicts (proceed) · `2` = conflicts **OR** missing baseline —
  *disambiguate by the output message* (`guardrails.baseline missing` ⇒ run `guardrails lock <folder>`
  first to adopt BASE; otherwise `CONFLICT …` lines ⇒ stop and have a human resolve) · `1` = a
  genuine error (missing folder/remote, corrupt baseline, invalid plan either side incl. GR2010).
- **`--apply`** materializes the merge in place and **re-writes the baseline**; it **must not run
  with conflicts present** (changes nothing, exits `2`). After apply: delete staging,
  `guardrails validate` (fix to green), `guardrails graph` (regenerate the stale diagram). Do
  **not** re-run `guardrails lock`.
- `--apply` leaves the generated `diagram.md` and harness-owned `state/` runtime **untouched**.
