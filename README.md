# Guardrails

**A reviewed markdown plan goes in. An executable task DAG with deterministic
acceptance checks comes out. A cross-platform harness runs it to green — retrying
failed tasks with the failure evidence fed back to the agent, and halting honestly
when a human is needed.**

The bet: in agentic engineering, *verification* is the bottleneck, not generation.
Guardrails lets a human review the **checks** once instead of reviewing **every
agent output** forever.

## The workflow

```
1. PLAN     agents + human write and extensively review  <plan>.md
2. BREAK    /plan-breakdown generates <plan>/ — tasks, dependencies, guardrails
            (inserting guardrail-enabling tasks the plan never mentioned, e.g.
            "author the unit tests" before "implement the feature")
3. REVIEW   the human edits guardrails; /guardrail-review attacks them:
            "what wrong implementation passes these?"
4. RUN      guardrails run <plan>/ — to green, or to an honest needs-human halt
```

Everything is plain files — git-diffable, PR-reviewable, no SaaS, no database:

```
my-plan/
├── guardrails.json              # run config (parallelism, retries, prompt runners)
├── state/seed.json              # optional initial shared state
└── tasks/01-author-tests/
    ├── task.json                # { description, dependsOn: [...] }
    ├── action.prompt.md         # or action.ps1 / action.sh / any executable
    └── guardrails/              # ALL must pass; exit 0 = pass
        ├── 01-tests-build.ps1
        └── 02-tests-fail-on-current-code.ps1
```

A **task** = one action (script, executable, or LLM prompt) + one or more
**guardrails** (deterministic checks preferred; LLM verdict-judges are a gated last
resort). If a guardrail fails, the harness composes actionable feedback and re-runs
the action — up to a retry budget, then marks the task `needs-human`, blocks only
its dependents, and lets independent branches finish. State flows between tasks as
immutable snapshots in, JSON fragments out — single-writer merged, crash-safe,
resumable.

## 60-second demo

```bash
git clone https://github.com/Servant-Software-LLC/Guardrails.git
cd Guardrails
dotnet run --project src/Guardrails.Cli -- validate examples/hello-guardrails/hello-guardrails
dotnet run --project src/Guardrails.Cli -- plan     examples/hello-guardrails/hello-guardrails
# the full run executes two LLM prompt tasks via Claude Code (~$1 of tokens):
dotnet run --project src/Guardrails.Cli -- run examples/hello-guardrails/hello-guardrails --fresh
```

The example produces a greeting validated end-to-end: a script action, two prompt
actions, state passing between tasks, deterministic guardrails, and one deliberate
prompt-judge — every moving part in three small tasks.

Prompt tasks run through a pluggable runner; v1 ships Claude Code headless
(`claude -p`), so the only prerequisites are the .NET SDK and (for prompt tasks)
[Claude Code](https://claude.com/claude-code). Deterministic-only plans need
nothing but .NET.

## CLI

| Command | Does |
|---|---|
| `guardrails validate [folder]` | Schema, DAG (cycles), file refs, interpreter/runner checks |
| `guardrails plan [folder]` | Execution-wave preview — runs nothing |
| `guardrails run [folder] [--fresh] [--no-ui] [--dry-run]` | Run to green; resume-aware; live progress table. `--dry-run` previews waves + per-task resolution + resume skips and exits without running |
| `guardrails status [folder]` | Journal table: per-task status, attempts, last failure |
| `guardrails reset [folder] [task]` | Re-arm one task, or wipe runtime state entirely |
| `guardrails skills install [--target <dir>] [--force]` | Copy the bundled skills into `~/.claude/skills` |

The `folder` argument is optional everywhere: omit it to use the current directory, so you
can `cd` into a plan folder and run `guardrails validate` (etc.) with no path. To reset one
task in the current directory, pass `.` explicitly: `guardrails reset . <task>`.

Exit codes: `0` green · `1` validation/harness error · `2` needs-human · `3` cancelled.

## The skills

`.claude/skills/` ships the agent-side tooling. The `guardrails` tool **bundles**
`plan-breakdown`, `guardrail-review`, and `guardrails-domain-knowledge` and installs
them into `~/.claude/skills/` via `guardrails skills install` (no manual copy):

- **plan-breakdown** — the generator. Sizes tasks (split where verification changes
  character), computes the sparsest correct DAG, selects guardrails
  deterministic-first via a catalogued decision tree, inserts guardrail-enabling
  tasks, self-validates, and always hands you a *draft*.
- **guardrail-review** — the adversary. Per task: "what's the cheapest wrong
  implementation that passes ALL of these?" Findings ranked BLOCKER/WEAK/NIT with
  ready-to-paste fixes.
- **uber-report**, **guardrails-domain-knowledge**, **guardrails-dev-knowledge** —
  status reporting and the knowledge base for agents working on this repo.

## Where things live

| What | Where |
|---|---|
| Mental model & principles | `docs/plans/01-overview.md` |
| **Every schema & contract (SSOT)** | `docs/plans/02-schemas-and-contracts.md` |
| Roadmap, Reality Gate, v2 bets | `docs/plans/03-roadmap.md` |
| Golden example | `examples/hello-guardrails/` |
| Harness source | `src/Guardrails.Core`, `src/Guardrails.Cli` (net8.0 dotnet tool) |

## Status

M1–M7 complete; the Reality Gate (build+tests, end-to-end example run on real
Claude, plan-breakdown round-trip) is met. M7 added run-level cost aggregation
(`Total prompt cost` on `run`/`status`), `run --dry-run` (waves + per-task resolution
+ resume-skip preview, touches no state), prompt-runner PATH probing in `validate`
(GR2009 warning), and packaging: the tool publishes to NuGet as
**`ServantSoftware.Guardrails`** (installs as the `guardrails` command) via a tag-driven
release pipeline.

```bash
# Windows one-liner: installs the tool + the bundled skills
irm https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.ps1 | iex

# or explicitly (any OS):
dotnet tool install --global ServantSoftware.Guardrails --prerelease
guardrails skills install          # copies the bundled skills into ~/.claude/skills
guardrails validate <plan-folder>
```

The first **dogfood** plan — a per-run cost cap (`docs/plans/04-dogfood-cost-cap.md` and
its validate-clean task folder) — is authored and awaiting human review before the
harness runs it on itself. v2 bets (worktree-per-task parallelism, CI mode, an executable
guardrail template library, cost caps) are specified in the roadmap.
