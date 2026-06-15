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

```jsonc
"promptRunners": {
  "default": "claude",
  "claude": {
    "command": "claude",                 // default: the runner name
    "permissionMode": "acceptEdits",     // default
    "allowedTools": ["Read", "Edit", "Write", "Grep", "Glob", "Bash(dotnet *)"],
    "maxTurns": 50,                      // default
    "model": null,                       // null = CLI default
    "extraArgs": [],
    "guardrailOverrides": {              // tighter verifier profile (partial override)
      "permissionMode": "default",
      "allowedTools": ["Read", "Grep", "Glob", "Write"],
      "maxTurns": 20
    }
  }
}
```

Scope `allowedTools` to what the plan's actions genuinely need — `Bash(dotnet *)` for
a .NET plan, not blanket `Bash`.

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
  "retries": 3,             // optional; overrides defaultRetries
  "timeoutSeconds": 3600,   // optional
  "exclusive": null         // optional; default: prompt action → true, script → false.
                            // Leave null unless you have a reason.
}
```

Task ids = folder names, `NN-verb-object` kebab-case; NN is a topological hint for
human scanning, `dependsOn` is the truth. `stableId` is OPTIONAL in the schema but the breakdown
**mints one per task by default** — it is the identity that survives a renumber/rename (the
regeneration merge's key, §11). Absent ⇒ identity falls back to the folder name; duplicate within
the plan ⇒ GR2010; a value not matching `^[a-z0-9][a-z0-9._-]*$` ⇒ GR2011. Omit the `action` block
when the task folder has exactly one `action.*` file (the convention); zero or multiple =
validation error.

## Prompt files (`.prompt.md`)

Optional YAML frontmatter: `description`, `runner`, `maxTurns`, `timeoutSeconds`.
Precedence for prompt actions: `task.json action.*` → frontmatter → runner config.

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
  `GUARDRAILS_STATE_OUT`; namespace keys under the task id. Invalid fragment = attempt fails.
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
