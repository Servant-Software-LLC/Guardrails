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
  "dependsOn": ["01-other-task"],                                // REQUIRED (may be [])
  "retries": 3,             // optional; overrides defaultRetries
  "timeoutSeconds": 3600,   // optional
  "exclusive": null         // optional; default: prompt action → true, script → false.
                            // Leave null unless you have a reason.
}
```

Task ids = folder names, `NN-verb-object` kebab-case; NN is a topological hint for
human scanning, `dependsOn` is the truth. Omit the `action` block when the task folder
has exactly one `action.*` file (the convention); zero or multiple = validation error.

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
