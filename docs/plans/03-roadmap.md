# 03 — Roadmap

Milestones M1–M7 build v1. Each has an **exit criterion** — demonstration-based,
not "code exists". The dogfood milestone is the ultimate guardrail-quality test:
weak guardrails would let the harness ship broken features into itself.

## v1 milestones

| # | Milestone | Exit criterion |
|---|---|---|
| M1 | **Foundations** — plan committed (`00-initial-plan.md`), `.gitignore`, docs 01–03, `guardrails-domain-knowledge` skill, `guardrails-architect` agent, hand-built `examples/hello-guardrails/` | Plan committed; schemas concrete enough that the example folder is fully spec'd |
| M2 | **Walking skeleton** — solution, `Guardrails.Core`/`.Cli` (net8.0, dotnet tool), PlanLoader/PlanValidator, ProcessRunner + InterpreterMap, serial executor, exit-code guardrails, `run` + `validate` | A 2-task script plan runs end-to-end on Windows + Linux; 3-OS CI matrix in place |
| M3 | **State + journal + resume** — StateManager (snapshot/fragment/merge), env contract, RunJournal, resume semantics, `status`/`reset`, per-attempt logs | Kill a run mid-flight, resume, completed tasks are skipped |
| M4 | **DAG + parallelism + retry** — Channel scheduler, cycle detection, maxParallelism, `exclusive`, retry + feedback composition, needs-human/blocked, Ctrl+C tree-kill, `plan` command, Spectre live UI | 4-task diamond: break a guardrail → exit 2 + blocked dependents; fix → resume green |
| M5 | **Prompts** — PromptComposer, frontmatter, IPromptRunner/registry/ClaudePromptRunner, verdict contract, feedback injection, fake-CLI tests + opt-in real-claude smoke | `hello-guardrails` runs fully green including prompt tasks |
| M6 | **Skills** — `plan-breakdown` (+ guardrail catalogue, schemas excerpt, worked example), `guardrails-review`, remaining agents, `guardrails-dev-knowledge`, `uber-report`, README | `/plan-breakdown` on `hello-guardrails.md` regenerates a validate-clean, structurally equivalent folder |
| M7 | **Polish + dogfood** — validation depth (interpreter/runner probes), cost reporting, `--fresh`/`--dry-run`, NuGet publish pipeline | Clean-machine acceptance (tool install + example green); a phase plan broken down, reviewed, and executed by the harness itself |

## Reality Gate (uber-report's honesty booleans)

1. `dotnet build` + `dotnet test` pass.
2. `guardrails run examples/hello-guardrails/hello-guardrails` completes end-to-end.
3. `plan-breakdown` output passes `guardrails validate`.

Until all three are true, any status report's verdict is capped at
**"walking skeleton not proven"**.

## v2 bets (named from day one; NOT v1 scope)

The four items that turn "useful for one agentic engineer" into "adopted by teams".
Each slots into an existing v1 seam — none invalidates the architecture:

1. **Worktree-per-task parallelism** — git worktree per task with merge-on-success
   replaces the v1 `exclusive`-by-default compromise; unlocks genuinely concurrent
   prompt tasks on one repo. Split into two layered issues: **#54 worktree mechanics
   + halt-on-conflict baseline** (worktree lifecycle, `WorkspaceLock`-exclusive
   merge-back, merge-queue visibility) and **#57 AI merge-conflict resolution** (a
   resolver sub-agent reconciles conflicts by default, gated by re-running the
   incoming task's guardrails, with its own in-progress UX + post-mortem log folder).
   *Seam: Scheduler/workspace resolution (workspace becomes per-task; merge step on
   `Succeeded`).*
2. **CI mode** — `guardrails run --ci` inside GitHub Actions emitting check runs /
   PR-per-task; journal published as a shared artifact. *Seam: an `IProgressSink`
   implementation + exit-code consumer; no core changes.*
3. **Executable guardrail template library** — the catalogue graduates from docs to
   parameterized templates (`tests-pass --filter X`, `port-answers 8080 /health`)
   that `plan-breakdown` instantiates instead of authoring scripts from scratch;
   raises guardrail quality, cuts generation variance. *Seam: a `templates/` dir +
   catalogue references.*
4. **Cost caps** — per-task/per-run token budget ceilings that trip needs-human
   instead of spending unattended (per-attempt cost is already logged in v1).
   *Seam: RetryPolicy/TaskExecutor budget check.*

## Seeded risk register (uber-report carries these every run)

1. Claude CLI contract instability — quarantined in `ClaudePromptRunner`; verdict
   files not exit codes; max-turns/timeout cost breakers.
2. Parallel tasks sharing one workspace — prompt actions exclusive-by-default;
   honest docs; worktrees are the v2 fix (#54 mechanics, #57 AI merge resolution).
3. Plausible-but-weak generated guardrails — "catches:" comments, prompt-judge
   demotion gate, tests-fail-on-stub, human review, `guardrails-review`, dogfooding.
4. Retry divergence (attempt N builds on attempt N−1's wreckage) — low default
   retries (2), "fix don't restart" feedback, full attempt logs.
5. Schema drift across docs/C#/skills — `02-schemas-and-contracts.md` is the SSOT;
   `validate` is the skill's exit gate; golden round-trip test in CI.
