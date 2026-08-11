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
3. REVIEW   the human edits guardrails; /guardrails-review attacks them:
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

## Installation

Guardrails ships as a cross-platform .NET tool on NuGet. Install it and its bundled
skills:

```bash
# Windows one-liner — installs the tool + the bundled skills:
irm https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.ps1 | iex

# or explicitly (any OS):
dotnet tool install --global ServantSoftware.Guardrails
guardrails skills install        # installs plan-breakdown + guardrails-review into ~/.claude/skills
```

No .NET on the machine? The prebuilt self-contained binaries bundle their own runtime:

```bash
brew install servant-software-llc/tap/guardrails      # macOS / Linux
curl -fsSL https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.sh | bash
```

**Prerequisites:** for the `dotnet tool` route, the
[.NET 10+ SDK](https://dotnet.microsoft.com/download) — the Homebrew and `install.sh`
routes need no .NET at all. For prompt tasks, [Claude Code](https://claude.com/claude-code)
installed and authenticated (the headless `claude -p` runner the harness drives).
Deterministic-only plans need nothing but .NET. Restart Claude Code after `skills install`
so it picks up the skills.

### macOS: downloaded the tarball in a browser?

The install routes above need no extra steps. Gatekeeper's notarization check fires on the
`com.apple.quarantine` extended attribute, which browsers, Mail, and AirDrop apply — but
`curl`, `git clone`, `dotnet tool install`, and `brew install` do not.

The one path that does hit it is clicking a release `.tar.gz` on the GitHub Releases page:
macOS quarantines it and you get *"cannot be opened because the developer cannot be
verified"*. Clear the attribute after extracting:

```sh
xattr -dr com.apple.quarantine ./guardrails
```

macOS 15 removed the Control-click → Open bypass; the GUI route is now
System Settings → Privacy & Security → **Open Anyway**.

## Quick Start

From a reviewed markdown plan to finished, verified work — run these in the repo whose
code the plan operates on:

```
1. Write your plan as a markdown file — a one-shot prompt or a full design doc.

2. /plan-breakdown path/to/your-plan.md
     → generates path/to/your-plan/: tasks, a dependency DAG, and deterministic
       guardrails — inserting guardrail-enabling tasks the plan never mentioned
       (e.g. "author the unit tests" before "implement the feature"). Hands you a DRAFT.

3. /guardrails-review path/to/your-plan
     → "what's the cheapest wrong implementation that passes these checks?" Ranked
       findings with ready-to-paste fixes. Edit the guardrails until you trust them.

4. guardrails run path/to/your-plan
     → runs the DAG to green, retrying failed tasks with the failure fed back to the
       agent, or halting honestly at needs-human. Resume-aware — re-run to continue.
```

Steps 2–3 run inside Claude Code (the skills you installed); step 4 is the `guardrails`
CLI. You review the **checks** once — not every agent output. `/plan-breakdown` also emits a
renderable `diagram.md` (or run `guardrails graph <folder>`) — a Mermaid view of the DAG.

## CLI

| Command | Does |
|---|---|
| `guardrails validate [folder]` | Schema, DAG (cycles), file refs, interpreter/runner checks |
| `guardrails plan [folder]` | Execution-wave preview — runs nothing |
| `guardrails graph [folder] [--check] [--stdout]` | Render a Mermaid diagram of the task/guardrail DAG to `<folder>/diagram.md`; `--check` reports staleness |
| `guardrails run [folder] [--fresh] [--no-merge-on-success] [--no-ui] [--dry-run] [--no-log-server] [--log-port <n>]` | Run to green; resume-aware; live progress table. **A green run DELIVERS to your branch by default** — see [Delivery on success](#delivery-on-success); `--no-merge-on-success` opts out. `--fresh` discards prior run state and starts over. While running, a localhost-only log server serves each task's live attempt log (each row carries a clickable **view log** link); `--no-log-server` disables it and `--log-port` pins the port. `--dry-run` previews waves + per-task resolution + resume skips and exits without running |
| `guardrails status [folder]` | Journal table: per-task status, attempts, last failure |
| `guardrails lock [folder] [--check] [--diff]` | Record or compare a plan folder's breakdown manifest (`guardrails.baseline`); `--check` reports drift via exit code, `--diff` prints the per-file classification |
| `guardrails merge [folder] --remote <dir> [--apply]` | Merge a freshly regenerated breakdown into the current folder, preserving human guardrail edits; `--apply` materializes it (otherwise dry-run report) |
| `guardrails logs [folder] [--port <n>] [--task <id>] [--no-open]` | Serve the web log viewer over a plan's persisted logs for post-mortem (any task — pass or fail); reads per-task status from the journal; opens a browser unless `--no-open`; runs until Ctrl-C |
| `guardrails reset [folder] [task]` | Re-arm one task, or wipe runtime state entirely |
| `guardrails skills install [--project] [--target <dir>] [--force]` | Copy the bundled skills into `~/.claude/skills` (or `./.claude/skills` with `--project`). `guardrails install skills` also works |

The `folder` argument is optional everywhere: omit it to use the current directory, so you
can `cd` into a plan folder and run `guardrails validate` (etc.) with no path. To reset one
task in the current directory, pass `.` explicitly: `guardrails reset . <task>`.

Exit codes: `0` green · `1` validation/harness error · `2` an actionable condition needing a human decision (e.g. `run` needs-human/blocked, stale `graph --check`, `lock --check` drift, `merge` conflicts) · `3` cancelled.

### Delivery on success

Tasks run in isolated git worktrees, never in your checkout. **When a run finishes green, the
harness merges the result into the branch you launched from** — that is the default, so a
successful run is a delivery, not just a report.

```bash
guardrails run <plan>/                          # green run -> merged into your branch
guardrails run <plan>/ --no-merge-on-success    # green run -> left on the plan branch; inspect first
```

Use `--no-merge-on-success` whenever you want to inspect before anything lands — a first run of a
freshly authored plan, a demo, or a plan that edits the repo you are working in. Nothing is merged
on a run that does **not** reach green: a needs-human halt, a failed gate, or a cancellation leaves
your branch untouched either way.

To make a plan never auto-deliver, set it in the plan instead of remembering the flag every time —
`"mergeOnSuccess": false` in its `guardrails.json`. Precedence, highest first: the CLI flag
(`--merge-on-success` / `--no-merge-on-success`) → `guardrails.json` → the default (on).

The AI-merge is still withheld at the boundary: the harness merges its own task branches, and hands
you anything it cannot resolve rather than guessing.

## The skills

`.claude/skills/` ships the agent-side tooling. The `guardrails` tool **bundles**
`plan-breakdown`, `guardrails-review`, and `guardrails-domain-knowledge` and installs
them into `~/.claude/skills/` via `guardrails skills install` (no manual copy):

- **plan-breakdown** — the generator. Sizes tasks (split where verification changes
  character), computes the sparsest correct DAG, selects guardrails
  deterministic-first via a catalogued decision tree, inserts guardrail-enabling
  tasks, self-validates, and always hands you a *draft*.
- **guardrails-review** — the adversary. Per task: "what's the cheapest wrong
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
| Harness source | `src/Guardrails.Core`, `src/Guardrails.Cli` (net10.0 dotnet tool) |

## From source (contributors)

Requires the **.NET 10 SDK or newer** — `global.json` accepts any SDK from 10.0.100 up, so a
current SDK is fine. The SDK carries the matching runtime, so `dotnet test` launches the
`net10.0` test host with no extra install and no roll-forward opt-in.

Working on Guardrails itself, or want to try the bundled example end-to-end?

```bash
git clone https://github.com/Servant-Software-LLC/Guardrails.git
cd Guardrails
dotnet run --project src/Guardrails.Cli -- validate examples/hello-guardrails/hello-guardrails
# the full run executes two LLM prompt tasks via Claude Code (~$1 of tokens):
dotnet run --project src/Guardrails.Cli -- run examples/hello-guardrails/hello-guardrails --fresh
```

`examples/hello-guardrails/` is the golden fixture — a script action, two prompt actions,
state passing, deterministic guardrails, and one deliberate prompt-judge, in three small
tasks: every moving part end-to-end. Its DAG is committed, pre-rendered, at
[`examples/hello-guardrails/hello-guardrails/diagram.md`](examples/hello-guardrails/hello-guardrails/diagram.md)
(GitHub renders the Mermaid inline) — the few-shot reference for what `guardrails graph` emits;
CI keeps it fresh with `graph --check`.
