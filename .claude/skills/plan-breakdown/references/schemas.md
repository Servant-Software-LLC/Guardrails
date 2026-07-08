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
└── tasks/<NN-verb-object>/
    ├── task.json
    ├── action.prompt.md | action.ps1 | action.sh | …   # exactly ONE action.* file
    └── guardrails/              # ≥1 guardrail or validation FAILS
        ├── 01-<what-it-proves>.ps1
        ├── 01-<what-it-proves>.json    # optional sidecar: description/args/timeoutSeconds
        └── 02-<what-it-proves>.prompt.md
```

Never generate `state/state.json`, `state/run.json`, or `state/logs/` — those are
harness-owned runtime artifacts (and gitignored).

`diagram.md` at the plan-folder root is also **generated, not hand-authored** —
emitted by `guardrails graph`, carrying a `<!-- guardrails:graph … source-sha256=… -->`
provenance comment whose hash is the staleness key. It is not plan input and must not
be hand-edited; the loader/validator ignore it. See SSOT §10 (Diagram artifact).

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
  "integrationGate": false, // optional, default false; true marks the terminal whole-repo
                            // integration gate — the final soundness boundary run once on the
                            // fully merged plan-branch HEAD (§3.3). ≥2 leaf tasks / any fan-in ⇒
                            // exactly one true sink (GR2017); the sink needs ≥1 scope:"integration"
                            // guardrail (GR2018).
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

`integrationGate` (optional, default **false**) marks a task as the terminal whole-repo integration
gate — the final soundness boundary run once on the fully merged plan-branch HEAD after every other
task succeeds (SSOT §3.3). Its guardrails are exactly the run's integration-guardrail set (all
`scope: "integration"` guardrails, §4.3). A plan with ≥2 leaf tasks or any fan-in MUST declare
**exactly one** `integrationGate: true` sink (**GR2017**); the sink MUST carry **at least one**
`scope: "integration"` guardrail (**GR2018**). A single linear chain with no fan-in may omit it.

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
