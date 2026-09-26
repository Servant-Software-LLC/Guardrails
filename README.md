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
            outside a Claude Code session: guardrails breakdown <plan>.md
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
installed and authenticated (the headless `claude -p` runner the harness drives), or the
Cursor Agent CLI (see [Running prompt tasks on Cursor](#running-prompt-tasks-on-cursor-instead-of-claude)).
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
| `guardrails breakdown <plan.md> [--out <dir>] [--runner-config <file>] [--force] [--no-validate]` | Author a plan folder from a plan `.md` — the same breakdown the JIT wave checkpoint runs, available as a verb so you do not have to be a Claude Code session to invoke `/plan-breakdown`. Writes beside the plan unless `--out`; refuses a `.charter.md` (flatten it with `charter handoff` first); **never marks the plan reviewed** — `/guardrails-review` still gates the run |
| `guardrails plan [folder]` | Execution-wave preview — runs nothing |
| `guardrails graph [folder] [--check] [--stdout]` | Render a Mermaid diagram of the task/guardrail DAG to `<folder>/diagram.md`; `--check` reports staleness. On a **waved** plan this covers every diagram the plan owns — the plan-level one *and* each `wave-NN-<slug>/diagram.md` |
| `guardrails run [folder] [--fresh] [--no-merge-on-success] [--no-ui] [--dry-run] [--no-log-server] [--log-port <n>]` | Run to green; resume-aware; live progress table. **A green run DELIVERS to your branch by default** — see [Delivery on success](#delivery-on-success); `--no-merge-on-success` opts out. `--fresh` discards prior run state and starts over. While running, a localhost-only log server serves each task's live attempt log (each row carries a clickable **view log** link); `--no-log-server` disables it and `--log-port` pins the port. `--dry-run` previews waves + per-task resolution + resume skips and exits without running |
| `guardrails status [folder]` | Whether the run is alive (`Run state:` line), then the journal table: per-task status, attempts, last failure |
| `guardrails lock [folder] [--check] [--diff]` | Record or compare a plan folder's breakdown manifest (`guardrails.baseline`); `--check` reports drift via exit code, `--diff` prints the per-file classification |
| `guardrails merge [folder] --remote <dir> [--apply]` | Merge a freshly regenerated breakdown into the current folder, preserving human guardrail edits; `--apply` materializes it (otherwise dry-run report) |
| `guardrails logs [folder] [--port <n>] [--task <id>] [--no-open]` | Serve the web log viewer over a plan's persisted logs (any task — pass or fail); reads per-task status from the journal; opens a browser unless `--no-open`; runs until Ctrl-C. Use it for a post-mortem **or to attach to a run already in flight** from another terminal — it serves what is on disk, which the running harness is still writing |
| `guardrails reset [folder] [task]` | Re-arm one task, or wipe runtime state entirely |
| `guardrails supply <plan> <path>...` | Stage file(s) for a resumable run — one with unfinished tasks, live or halted — to pick up at its next drain boundary: the **next task boundary** while the run is still executing, or the **run-start boundary** on the next `guardrails run` if it has already halted and exited. Each `path` is **workspace-relative** — the staged layout *is* the destination layout, so there is no separate destination argument. A task agent may supply only paths inside its own `writeScope`; an operator invocation is unrestricted |
| `guardrails telemetry ingest [folder]` · `report` · `purge` | Read, summarize or erase the **local** record of what your runs cost and which model ran them — see [Local telemetry](#local-telemetry). `ingest` backfills from runs already on disk; a run ingests itself automatically at the end |
| `guardrails skills install [--project] [--target <dir>] [--force]` | Copy the bundled skills into `~/.claude/skills` (or `./.claude/skills` with `--project`). `guardrails install skills` also works |
| `guardrails attach [folder]` | Attach a **second terminal** to a run's live progress table, replaying its recorded events. Read-only — it never touches the run — and it works both while the run is in flight and after it has finished. This is how you watch an unattended run without being the terminal that launched it |
| `guardrails samples verify [folder]` | Execute every committed `tasks/<id>/samples/` pair against its guardrail and report the findings. Worth knowing about *before* a run: the same check runs as a **pre-DAG gate**, so a broken pair halts the run before task one |
| `guardrails mark-reviewed [folder] [--evidence <report>] [--source <kind>]` | Record that `/guardrails-review` ran, clearing the GR2025 "not reviewed" nudge. The marker is keyed on the plan's definition hash, so editing any guardrail body re-stales it. `--evidence` points at the written report and records a stronger attestation class than a bare stamp |
| `guardrails plan-hash [folder]` | Print the plan's `PlanDefinitionHash` (or one wave's) — read-only. This is the hash the review flow embeds in its report |
| `guardrails providers init [folder] [--write]` · `check <block>` | Inspect and annotate the prompt-runner registry in a plan's `guardrails.json`. `init` previews a diff and writes nothing until `--write` |
| `guardrails diagnostics [<code>] [--ladder GR20]` | Explain the `GR` codes `validate` emits — severity plus the full rationale. Read-only, offline, **no plan folder needed**, so it still works when the plan does not |

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
freshly authored plan, a demo, or a plan that edits the repo you are working in; on a waved plan
(below) it also stops every delivery point from delivering, not only the run-end merge, and a task
definition edited mid-run blocks a delivery point's delivery the same way it blocks the run-end
merge (#556). Nothing is merged before a plan's first delivery point is reached: a needs-human
halt, a failed gate, or a cancellation before that leaves your branch exactly as it was, whether
the plan is flat (one delivery point, at the end) or waved. Once a wave **has** delivered, though,
that work is already on your branch and stays there no matter what a later wave does — see the
partial-delivery report, below.

To make a plan never auto-deliver, set it in the plan instead of remembering the flag every time —
`"mergeOnSuccess": false` in its `guardrails.json`. Precedence, highest first: the CLI flag
(`--merge-on-success` / `--no-merge-on-success`) → `guardrails.json` → the default (on).

The AI-merge is still withheld at the boundary: the harness merges its own task branches, and hands
you anything it cannot resolve rather than guessing.

**Per-wave delivery.** A wave opts in by setting `delivers: true` in its `brief.md` YAML front
matter (default `false`):

```yaml
---
delivers: true
---
```

A delivering wave is a **delivery point**: once its exit gate passes, the plan branch as it
stands — this wave plus every non-delivering wave before it — merges into your branch right there,
at that wave's barrier, instead of waiting for the run to finish. **The plan's final wave is the
exception**: it always delivers at the end of the run, after the plan-level terminal gate
(`<plan>/guardrails/`) passes, so work never lands on your branch ahead of a terminal gate that
then fails. Earlier waves deliver before that gate can even run, so if it fails after they
delivered, `run.json` records the run `partially-delivered` (below). A plan that marks no wave
`delivers` behaves exactly as it does today — one merge at run end.

There is no per-wave config file for this: `brief.md` is the only place the flag can live. A
`guardrails.json` dropped inside a wave directory is silently ignored — the plan stays waved and
`validate` does not warn — so an operator who guesses that file gets no delivery and no error.

**The interlock is wave-scoped, and a held wave's work rides along.** A `proceeded-best-guess` or
`proceeded-unreviewed` decision — the same judgment calls that hold back an unattended run's
end-of-run delivery — is now checked at every delivery point, against every wave that delivery
carries. A delivery is held when ANY wave it carries recorded such a decision, not only the
delivering wave, so once a wave is held every later delivery is held too until the run ends,
unless you force delivery past that decision with `--merge-on-success`.

**A refused delivery halts the run at that wave**, instead of going on to later waves whose
delivery would be refused the same way. A delivery is refused when your checkout has moved to
another branch, your branch gained commits after the trial merge was built (you kept working while
the gate ran), the merge conflicts, or your working tree has changes the merge would overwrite.
Deliveries that already landed stay on your branch, and your checkout itself is never touched. The
wave is not marked complete until its delivery settles, so resuming after you fix the cause
re-attempts that wave's delivery. The two branch causes need different remedies: a checkout
switched to another branch needs that branch checked out again before you resume, while a branch
that simply advanced after the trial was built needs only a resume, since the next trial includes
your new commits.

**Once a wave has delivered, the rest of the plan goes to the same branch.** The harness records the
branch the first delivery landed on and compares against that one from then on, so resuming from
somewhere else — another branch, or a detached `HEAD` — is refused rather than quietly delivering the
rest of the plan there and splitting its work across two branches. The halt names both branches and
tells you to check the first one out again. This is enforcement of the rule above, not a new one to
learn. `run.json` records the refusal in `decisions[]` as `delivery-refused`, which the
console shows; the log site, though, shows only the wave as needs-human — there is no log-site
panel for a refused delivery in this version.

**A failed exit gate on the trial merge halts like any other failed exit gate.** When the wave's
exit gate fails on the trial merge with your new commits, the run halts exactly as it does for any
failed exit gate — the halt banner, `run.json`'s `halt` section, and the gate logs — and the
headline says the gate failed on the merge with your branch, naming your branch's tip and a
`git log <plan-tip-sha>..<your-tip-sha>` range so you can see exactly which of your commits it
merged; it is sha-keyed, so it stays accurate even after either branch moves again. `run.json`
records that wave's delivery outcome as `trial-gate-failed`.

**A rejecting git hook holds delivery instead of halting the run.** If your git hook rejects the
trial merge commit, this delivery and every later one wait for the end of the run, where the final
merge runs your hooks in your own checkout. A hook that needs untracked tooling, such as
`node_modules` for husky with lint-staged, can fail in the harness's trial worktree and pass in
yours — that is why the hold, not a halt. If that final merge lands, the run is delivered.
`--merge-on-success` does **not** lift this hold: the override is for a delivery a suppressing
decision held, not one a rejecting hook held. The end-of-run report names the wave whose hook
rejected the merge, the hook's message, and every wave held alongside it, so even a green,
delivered run still tells you incremental delivery was held back along the way.

**The merge commit runs your git hooks.** A delivering wave's exit gate runs against a trial
merge, and when your branch has moved on, that merge commit is created with your git hooks,
exactly as today's run-end merge commit already is. Hooks installed under a relative
`core.hooksPath` — the way husky installs them — run too.

**The partial-delivery report.** When an earlier wave has delivered and a later one fails, the run
prints which waves landed on your branch and which are held on the plan branch, before the
verdict — the exit code does not change, so a run with a failed wave is still a failed run.
`git branch --no-merged`, already your check for whether a run shipped, stays the confirmation.
`run.json`'s delivery record says the same thing in machine-readable form: its outcome is
`partially-delivered` with `delivered: false`, because `delivered` is only ever `true` when every
bit of verified work reached your branch. The one case that still reads fully `delivered`: a run
whose final merge lands after a rejecting hook held every delivery — that merge carries every held
wave along with it.

**The post-delivery refresh.** If your branch moved on its own between deliveries, the harness
merges that motion back into the plan branch right after delivering, so later waves build on your
new commits instead of an increasingly stale base. That admits content no task in the plan
authored, so `run.json` records it in `refreshed[]`, and if a later gate then fails over that tree,
the failure names the refresh rather than blaming the wave that did nothing wrong.

**Two new `validate` warnings.** `GR2078` fires when a wave right after a delivery point carries no
entry preflight of its own. `GR2079` fires when a wave sets `delivers: true` but carries no exit
gate, so it can never actually become a delivery point. Both are warnings — neither moves the exit
code.

### Running unattended

A long plan is usually not watched. `--autonomous` is how that is run:

```bash
guardrails run <plan>/ --autonomous --dial standard --max-cost-usd 60
```

- `--autonomous` lets the run answer its own checkpoints instead of stopping to ask. Without it a
  wave barrier or a needs-human halt waits for a human who may not be there.
- `--dial` sets the **lowest criticality that still escalates to a human** — `low`, `moderate`, `high`,
  or `critical`. Raising it means fewer things stop the run, and `critical` is fully autonomous.
- `--max-cost-usd` is the ceiling. **`--autonomous` applies a $20 cap when you do not pass one**,
  which is deliberate — an unattended run with no ceiling is an unbounded bill — but it is easy to
  meet by surprise on a real plan, and the run halts when it does. Pass the number you actually mean.

Watch it from anywhere with `guardrails attach <plan>/`, which tails the run's recorded
`logs/<runId>/observer.jsonl` into a live table in a second terminal without touching the run.

**A green run is not automatically a delivered run.** If you launched with `--no-merge-on-success`,
the work is complete and sitting on the plan branch; the summary says so at the end. Check with
`git branch --no-merged` before assuming it shipped.

### Running prompt tasks on Cursor instead of Claude

Prompt tasks run on Claude Code by default. From **v1.22.0**, they can run on
[Cursor's Agent CLI](https://cursor.com/docs/cli/overview) (`agent`) instead. That helps when your
Claude account has hit a usage or spend limit but Cursor is installed on the same machine.

**1. Install and sign in to the Cursor CLI**, then check that `agent` is on your PATH:

```bash
curl https://cursor.com/install -fsS | bash                    # macOS / Linux
irm 'https://cursor.com/install?win32=true' | iex              # Windows (PowerShell)
agent login
agent --list-models                                            # model ids you can put in "model"
```

**2. Replace the plan's `promptRunners` in `guardrails.json` with one `cursor` block:**

```json
"promptRunners": {
  "default": "cursor",
  "cursor": {
    "kind": "cursor",
    "model": "claude-opus-5-5-high"
  }
}
```

- `command` defaults to `agent`. Set it to a full path if `agent` is not on the PATH that
  `guardrails` sees.
- Leave out `model` to use Cursor's own default ("Auto").
- Use a **single** block, not a Cursor block added next to the Claude ones. A task still goes to
  Claude when a Claude block declares `routing` (tier routing), when the task sets `action.runner`,
  or when its prompt's front matter sets `runner:`. With only a `cursor` block left, every one of those
  leftover references fails `guardrails validate` by name, so you can find and repoint them rather than
  having a task quietly run on Claude.
- To go back to Claude, restore the original block.

**Choose how Cursor approves commands: `approvalMode`.** Cursor has no per-tool allowlist that works in
headless mode (a project `.cursor/cli.json` allowlist has no effect there), so this one setting decides what
the agent may run:

| `approvalMode` | Cursor flag | What happens |
|---|---|---|
| `"force"` (default) | `--force` | Every command runs (Cursor's "Run Everything"). |
| `"auto-review"` | `--auto-review` | Cursor's server-side reviewer approves or refuses each command. Writes, `git` and `dotnet` ran in testing. |
| `"none"` | none | Files are written, but Cursor refuses **every** shell command unless you also add `"extraArgs": ["--sandbox", "enabled"]`, which runs them in Cursor's sandbox. |

**Enterprise teams whose administrator disabled "Run Everything"** can't use the default. Cursor refuses
to start and prints `Your team administrator has disabled the 'Run Everything' option`. Guardrails stops
the task right away as needs-human, quotes that message, and tells you what to change. It doesn't spend
your retries. Use `"auto-review"` instead:

```json
"cursor": { "kind": "cursor", "approvalMode": "auto-review" }
```

Or use `"none"` with the sandbox: `"approvalMode": "none", "extraArgs": ["--sandbox", "enabled"]`. Inside
the sandbox, network access follows your team's sandbox settings, so a `dotnet restore` or `npm install`
may be blocked. Don't put `--force` (or `-f`), `--yolo` or `--auto-review` in `extraArgs`. `validate` rejects them
(`GR2082`), because `approvalMode` owns that flag, and Cursor itself refuses `--auto-review` together
with `--force`.

**Refused commands are never reported as success.** When Cursor refuses a command, the session can still
end with "success". Guardrails reads each tool call's own result. A command that Cursor started but never
finished before the session ended also counts as refused. Under `"auto-review"` this happens when Cursor's
reviewer holds a command for an approval that headless mode can't give, such as the agent's own `git commit`.
Such a command is reported as `abandoned: started but never completed`. It's named like any refused
command, but it doesn't count toward "every shell command was refused" for a task's own agent, so the task
retries as usual. A Cursor prompt guardrail (a judge) whose commands were all refused or abandoned still fails.
If Guardrails stopped the session itself (a timeout or a stall), a command that was still running is not
treated as refused. The retry feedback lists it as still running, and the attempt keeps its own failure
reason.

- If **every** shell command the agent tried was refused, the task's guardrails still run, because the
  edits may be right. If they pass, the task succeeds and its summary lists the refused commands. If they
  fail, the task stops as needs-human right away, without spending retries, and names each command, the
  reason, and the `approvalMode` change to make.
- If only **some** were refused, the attempt continues and the task's guardrails decide. Each refused
  command and its reason appear in the attempt summary and in the retry feedback.
- A Cursor **prompt guardrail** (a judge) that couldn't run any shell command fails, even if it wrote a
  passing verdict.

A reason like `Hook blocked with message: …` comes from a hook, not from Cursor's policy. Cursor's
**"Include Third-Party Configs"** setting imports your Claude Code configuration from `~/.claude`,
including its hooks, so a Claude hook can refuse Cursor's shell calls. Fix or disable that hook, or turn
the setting off.

**3. Validate, then run as usual:**

```bash
guardrails validate <plan>/
guardrails run <plan>/
```

**What changes on Cursor.** `validate` prints one `GR2080` warning per `cursor` block, meaning the
block runs without Claude's per-tool controls. It is expected. Here is what it means:

- **No per-tool controls.** Cursor has no per-tool allowlist, so what the agent may run is set by
  `approvalMode` alone. The warning says what your chosen mode allows. `permissionMode`,
  `allowedTools`, `maxTurns` and `maxOutputTokens` do nothing on this block, and the warning names any
  of them you left in.
- **Weaker containment.** The harness's diff checks cover the task's worktree. Cursor can't load
  the Claude hook that confines writes to that worktree. A task that edits its own task
  definition (its `task.json`, action prompt or guardrail files) fails.
- **No cost figures.** Cursor reports token counts but no cost, so the `--max-cost-usd` ceiling (and
  the $20 `--autonomous` default) never trips on a Cursor run. Watch spend in your Cursor account.
- **Read-only helpers switch off.** The overwatcher, needs-human AI triage and the autonomy
  criticality judge never run on a Cursor block. When they would fall back to the `cursor` block, the run
  turns them off and says so at startup. Declare a Claude block named `overwatch` or `ai-triage` to
  keep them.
- **Delivery is checked.** The harness sends the prompt on stdin and checks Cursor's echo of it. If
  Cursor did not receive the prompt, the attempt fails. For example, a bare word in `extraArgs` would
  make Cursor ignore stdin. That attempt is never reported as success.

### Running prompt tasks on a local model through a gateway

Prompt tasks can run on Claude Code pointed at a local model, such as Qwen served by
[`llama-server`](https://github.com/ggml-org/llama.cpp), through an Anthropic-compatible gateway such as
[LiteLLM](https://docs.litellm.ai/). Give a `claude` block a `baseUrl`, and Guardrails configures the Claude
Code child itself. You don't need a `claude-local` wrapper script. You still start `llama-server` and LiteLLM
yourself; Guardrails never starts either one.

> Anthropic documents gateways for its own models. Running Claude Code against a non-Claude model through a
> gateway is outside what Claude Code supports. It works, and Guardrails checks as much of it as it can before
> any task runs, but treat it as experimental.

**1. Start one `llama-server` per model, and LiteLLM in front of them.** One `llama-server` holds one model,
so Qwen 3.6 and Qwen 3.8 each need their own server on their own port. Adjust the model paths to yours:

```bash
llama-server -m /models/Qwen3.6-35B-A3B-Q4_K_M.gguf --alias Qwen3.6-35B-A3B --jinja -c 65536 -np 1 --port 8080
llama-server -m /models/Qwen3.8-27B-Q4_K_M.gguf     --alias Qwen3.8-27B     --jinja -c 65536 -np 1 --port 8081
```

`-c 65536 -np 1` gives the single slot a 65,536-token window. With `-np N`, each slot gets `-c` divided by `N`,
and that per-slot figure is what goes in `contextTokens` below.

```yaml
# litellm.yaml
model_list:
  - model_name: Qwen                       # what the Guardrails block's "model" names
    litellm_params:
      model: openai/Qwen3.6-35B-A3B
      api_base: http://127.0.0.1:8080/v1
      api_key: none
  - model_name: Qwen3.8
    litellm_params:
      model: openai/Qwen3.8-27B
      api_base: http://127.0.0.1:8081/v1
      api_key: none
```

```bash
litellm --config litellm.yaml --port 4000
```

If your LiteLLM has a `master_key`, export it under a name of your choice before you run Guardrails, for example
`export LITELLM_KEY=sk-...` (PowerShell: `$env:LITELLM_KEY = 'sk-...'`), and add `"authTokenEnv": "LITELLM_KEY"`
to the block. Put the variable's **name** in `guardrails.json`, never the key itself.

**2. Point the plan's `promptRunners` at the gateway:**

```json
"maxParallelism": 1,
"promptRunners": {
  "default": "qwen36",
  "qwen36": {
    "kind": "claude",
    "command": "claude",
    "baseUrl": "http://127.0.0.1:4000",
    "model": "Qwen",
    "backendModel": "qwen3.6-35b-a3b",
    "contextTokens": 65536
  },
  "qwen38": {
    "kind": "claude",
    "command": "claude",
    "baseUrl": "http://127.0.0.1:4000",
    "model": "Qwen3.8",
    "backendModel": "qwen3.8-27b",
    "contextTokens": 65536
  }
}
```

- Keep `"command": "claude"`. A block's `command` defaults to the block's name, so without it Guardrails
  would try to launch a program called `qwen36`.
- `baseUrl` is the gateway's root. Don't add `/v1`; Claude Code appends `/v1/messages` itself.
- `model` is required. It must be a name in LiteLLM's `model_list`, and every Claude Code model alias,
  background model and subagent model is pinned to it.
- `backendModel` is optional but recommended. It is what the server behind the gateway must have **loaded**,
  and the run stops before any task if it doesn't match. Include the version (`qwen3.6-35b-a3b`, not `qwen`).
- `contextTokens` is optional. It tells Claude Code to compact before the backend's per-slot window fills, and
  the run stops if it is larger than the slot.
- `permissionMode`, `allowedTools`, `maxTurns`, `env` and `extraArgs` work as on any `claude` block, with two
  exceptions: `ANTHROPIC_*` and the other variables Guardrails sets, and `--settings`, are rejected by
  `validate` (`GR2084`).
- Keep `maxParallelism` at 1 when two models share one gateway. `validate` warns otherwise (`GR2086`).
- A Claude model name (`sonnet`, `claude-…`) that would reach a gateway block, for example through a task's
  `action.model`, draws a `GR2085` warning.

**3. Validate, check tool calling, then run:**

```bash
guardrails validate <plan>/
guardrails providers check <plan>/ qwen36     # a tool_use round trip through the gateway
guardrails run <plan>/
```

**What the run checks before any task starts.** A plan with a gateway block runs a preflight, and any failure
stops the run with the reason, before a task spends a turn:

- your machine's managed Claude Code settings, and the target repo's `.claude/settings.json` and
  `.claude/settings.local.json`, don't set anything that could send requests elsewhere or with another
  credential, such as `ANTHROPIC_BASE_URL`, `ANTHROPIC_API_KEY`, `CLAUDE_CODE_USE_BEDROCK` or `apiKeyHelper`.
  The message names the file and the key. Every managed source Claude Code documents is read — the
  `managed-settings.json` file and `managed-settings.d/` drop-ins, the Windows `HKLM`/`HKCU` policy registry value,
  and the macOS `com.anthropic.claudecode` configuration profile. Server-managed settings (pushed from the
  claude.ai console) can't be read from here, and the run header says so. The integration worktree a resumed run
  continues from is checked too;
- the `authTokenEnv` variable, if any, is set;
- the gateway answers, lists each declared model, and returns a real reply to a short message;
- the server behind each model has the `backendModel` you declared loaded, `contextTokens` fits its per-slot
  window, and no two model names are served by the same loaded model. That includes a model a task pins with
  `action.model`: every model a run can send to the gateway is checked. The server is asked what it loaded
  WITHOUT your gateway key, and only when it is on this machine or a private network address; a server elsewhere
  is reported as not probed rather than sent a request. If you start `llama-server` with `--alias`, keep the alias
  honest: the match trusts it.

The run header then shows the identity it found, for example
`Gateway: block 'qwen36' → http://127.0.0.1:4000, model 'Qwen': backend http://127.0.0.1:8080 Qwen3.6-35B-A3B (backendModel 'qwen3.6-35b-a3b' matched).`
When the gateway isn't LiteLLM, or the server doesn't say what it loaded, it reads `backend identity
unverified`, and a declared `backendModel` is reported as "declared, not verified", never as matched. This check
runs once, at the start. A server that swaps models mid-run isn't detected. `guardrails breakdown --runner-config`
and `guardrails run --revalidate-task` run the same checks before they use a gateway block.

**What changes on a gateway block:**

- **Tokens instead of cost.** Claude Code prices every call from Anthropic's list, which is meaningless for a
  local model, so Guardrails records no cost and shows token usage wherever it would show a dollar figure:
  `48.2k tok (gateway)`. A mixed run shows both, for example `Total prompt cost: $1.8400 + 310.5k tok (gateway)`.
  A gateway dispatch that reported no token usage is counted, not dropped: `+ 1 dispatch(es) without usage (gateway)`.
  `--max-cost-usd` (and the $20 `--autonomous` default) doesn't limit gateway spend; the run says so at startup.
- **A clean Claude Code profile.** Each run gives Claude Code its own empty config directory under
  `logs/<runId>/claude-config/`, so your `~/.claude` settings, `CLAUDE.md`, skills, memory, MCP servers and
  stored login aren't used, and session transcripts land there instead of `~/.claude/projects/`. Claude Code
  also starts without a record of trusting the repo, so some project settings may not apply. Treat the block's
  `allowedTools` as the agent's whole permission grant. That directory is served by the local log viewer with the
  rest of the run's logs, so anything an agent prints into its session — including a token — can be read there.
  All of a run's gateway sessions share its one `.claude.json`; at `maxParallelism` above 1, watch for it being
  corrupted.
- **The token is visible to the agent's shell commands.** Commands the model runs inherit the child's
  environment, including `ANTHROPIC_AUTH_TOKEN` and the variable `authTokenEnv` names (which Guardrails leaves in
  place). Without `authTokenEnv` the token is a harmless placeholder. With a real key for a remote gateway, any
  command the model runs can read it.
- **Some traffic may still leave the machine.** Guardrails turns off Claude Code's nonessential traffic, but
  some features (such as fast-mode checks and WebFetch's safety check) are documented to call
  `api.anthropic.com` directly, and whether that switch stops them is unverified.
- Every provenance record, telemetry row and event for a gateway dispatch names the gateway and the backend
  identity, so a local-model attempt can always be told apart from a Claude one.

The full contract is in [`docs/plans/02-schemas-and-contracts.md`](docs/plans/02-schemas-and-contracts.md) §9.10.

### Local telemetry

**Every run records what it cost and which model ran each task, into a file on your own machine.**
This is on by default, so you should know it is there:

- **Where:** `~/.guardrails/telemetry/` — append-only JSONL, one file per month.
- **What:** per attempt — the model, runner, tier and effort that ran it, its outcome, timings, token
  counts and cost. Facts and identifiers only: **no prompt text, no file contents, no diffs, no
  absolute paths.**
- **Nothing is transmitted anywhere.** There is no upload path in the design. It is a local file you
  can read, grep, or delete.
- **Off switch:** set `GUARDRAILS_TELEMETRY=off`. `guardrails telemetry purge` erases what is already
  there.

It is on by default because it is cheap to store and only becomes useful once there is enough of it:
a corpus you have to remember to switch on is a corpus that is empty on exactly the machines that
would benefit. `guardrails telemetry report` turns it into a per-model, per-tier comparison — how
often a model gets it right first time, how many attempts it needs, what that costs — which is what
makes "should this task run on a cheaper model?" a question with an answer instead of a guess.

The contract is `docs/plans/02-schemas-and-contracts.md` §15.

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

## What a plan folder holds

Two things an author meets on day one and will not find anywhere else in this file:

**`writeScope` is required on every task.** It lists the paths that task is allowed to write, and the
harness enforces it — an edit outside the declared scope is stripped, not merged. A task without one
fails validation (`GR2041`). It is the mechanism behind most of the isolation guarantees here: it is
what lets an implementation task be forbidden from editing the tests that judge it.

**Checks live in four folders, and which one you pick decides when the check runs:**

| Folder | Runs |
|---|---|
| `<plan>/preflights/` | Once, BEFORE any task is scheduled — a baseline. Use it to assert the area is green before work starts |
| `<plan>/guardrails/` | Once, at the END, on the merged result — the terminal gate |
| `tasks/<id>/preflights/` | Before that task's action |
| `tasks/<id>/guardrails/` | After that task's action — the usual place |

A run also writes two event streams under `logs/<runId>/` — `events.jsonl` and `observer.jsonl`; their
schemas are `docs/plans/02-schemas-and-contracts.md` §8.1 and §8.2. `guardrails attach` tails the
second.

**Plan-folder edits — task prompts, guardrails — reach a running plan live; code artifacts do not** — a
code file the harness needs on the run's base gets there only through `guardrails supply`.

## Where things live

| What | Where |
|---|---|
| Mental model & principles | `docs/plans/01-overview.md` |
| **Every schema & contract (SSOT)** | `docs/plans/02-schemas-and-contracts.md` |
| Roadmap, Reality Gate, v2 bets | `docs/plans/03-roadmap.md` |
| Golden example | `examples/hello-guardrails/` |
| Harness source | `src/Guardrails.Core`, `src/Guardrails.Cli` (net10.0 dotnet tool) |
| Local telemetry corpus (yours, never transmitted) | `~/.guardrails/telemetry/` |

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
