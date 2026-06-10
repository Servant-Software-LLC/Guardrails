# Guardrails — Plan-to-Tasks Harness + Plan-Breakdown Skill

## Context

Agentic engineering workflow today: agents + human produce a reviewed markdown plan, then execution is ad hoc. **Guardrails** closes the loop: a `plan-breakdown` skill converts a reviewed plan into a file-system folder of **tasks** (DAG with `dependsOn`), each with an **action** (exe/script/prompt) and one or more **guardrails** (exe/script/prompt — ALL must pass). A cross-platform C#/.NET **harness** (`guardrails` dotnet tool) executes the DAG: run action → run guardrails → on failure, retry the action with guardrail-failure feedback up to N times → halt that branch as `needs-human` while independent branches finish. Humans review/edit the generated folder before execution; a `guardrail-review` skill provides an adversarial second pass. Source notes: `C:\Users\David\Downloads\Guardrails thoughts on paper.pdf`. Repo: `C:\Dev AI\Guardrails` (fresh — `.git` + README only). The `.claude/` setup mirrors `C:\Dev AI\MaltbyGenealogy\.claude` house style.

## Decisions (user-confirmed)

1. **Prompt execution**: Claude Code CLI headless (`claude -p`) now; pluggable `IPromptRunner` abstraction so other CLIs (codex/gemini) plug in later via config.
2. **Layout**: flat task folders + explicit `dependsOn` in `task.json` (no nesting, no noop folders).
3. **State**: snapshot-in / fragment-out. Each process gets a read-only merged-state snapshot (`GUARDRAILS_STATE_IN`) and writes its own fragment (`GUARDRAILS_STATE_OUT`); the harness is the single writer of `state/state.json`, deep-merging fragments in completion order. No locks in child processes; crashed tasks can't corrupt state.
4. **Distribution**: global dotnet tool (`dotnet tool install -g guardrails`) + thin Claude skill wrapper. Skills are usable from other repos by copying into `~/.claude/skills/` (documented in README).

Devil's-advocate resolutions: prompt actions default `exclusive: true` (sole workspace access; opt-out per task); zero-guardrail tasks fail validation; `Succeeded` is terminal on resume (`guardrails reset <task>` to force re-run); `validate` probes interpreters/runners before any run.

**Plan lives in the repo.** Step one of implementation: copy this plan into the repo as `docs/plans/00-initial-plan.md` and commit it — it is the plan-of-record seed, later refactored into the numbered docs below (which supersede it section by section). The repo currently has no `.gitignore`; when the standard .NET one is added, verify it does NOT exclude `docs/`, `.claude/` (agents/skills are deliverables), or `examples/` — only build output (`bin/`, `obj/`, `nupkg/`) and runtime artifacts of example runs (`examples/**/state/run.json`, `examples/**/state/logs/`, `examples/**/state/merge-conflicts.log`; the seeded `state.json` and the golden task folders stay committed).

## Repo layout

```
C:\Dev AI\Guardrails\
├── README.md                  # pitch, 60-second demo, the 4-stage workflow diagram
├── Guardrails.sln
├── docs\plans\                # 00-initial-plan (this plan, committed first), then 00-overview,
│                              # 01-harness-architecture, 02-schemas-and-contracts (SSOT), 03-roadmap
├── src\
│   ├── Guardrails.Core\       # domain, scheduler, state, runners — no UI deps
│   └── Guardrails.Cli\        # dotnet tool: PackAsTool, ToolCommandName=guardrails
├── tests\
│   ├── Guardrails.Core.Tests\         # unit: scheduler/merge/retry/loader with fake runners
│   └── Guardrails.Integration.Tests\  # real processes, fixture plans, fake claude CLI
├── examples\hello-guardrails\ # hello-guardrails.md + hand-built generated folder (golden fixture)
└── .claude\
    ├── agents\                # guardrails-architect, -harness-developer, -skill-author,
    │                          # -test-author, -devils-advocate
    ├── skills\                # plan-breakdown, guardrail-review, guardrails-domain-knowledge,
    │                          # guardrails-dev-knowledge, uber-report
    └── tasks\                 # uber-report output
```

## Generated plan-folder format (the product's contract)

```
plan-name/                       (created next to plan-name.md)
├── guardrails.json              # run config (below)
├── state\
│   ├── state.json               # merged state (harness-owned)
│   ├── run.json                 # journal: per-task status + attempts (resume substrate)
│   └── logs\<task>\attempt-N\   # state-in snapshot, action stdout/err, guardrail outputs, feedback.md
└── tasks\<NN-verb-object>\
    ├── task.json                # { description, dependsOn:[], retries?, timeoutSeconds?, exclusive?, action? }
    ├── action.prompt.md | action.ps1 | action.sh | ...   # convention-discovered if task.json omits "action"
    └── guardrails\
        ├── 01-build-passes.ps1            # exit 0 = pass; print one-line actionable reason on failure
        ├── 01-build-passes.json           # optional sidecar: description/args/timeout
        └── 02-review.prompt.md            # YAML frontmatter for metadata; writes verdict JSON
```

**Key schema points** (full schemas in `docs/plans/02-schemas-and-contracts.md`):
- `guardrails.json`: `maxParallelism`, `defaultRetries` (2), `defaultTimeoutSeconds`, `guardrailMode` (`failFast` default | `runAll`), `workspace` (cwd for child processes, default `..`), `interpreters` map (`.ps1`→pwsh w/ powershell.exe fallback, `.sh`→bash, `.py`→python3→python, `.cmd` Windows-only, `.dll`→dotnet; `{script}`/`{args}` tokens), `promptRunners` (named configs: command, permissionMode, allowedTools, maxTurns, guardrailOverrides — tighter read-mostly profile for verdict prompts).
- Guardrails ordered by filename sort, cheapest-first by convention. Minimum 1 per task (validation error otherwise).
- **Verdict contract** (prompt guardrails): must write `{ "pass": bool, "reason": string }` to `GUARDRAILS_VERDICT_OUT`. Missing/invalid verdict = fail. Never trust `claude -p` exit codes for semantic pass/fail.
- **Fragments**: any JSON object, conventionally namespaced under the task's own key. Invalid fragment = attempt failure (retried). Deep merge, scalars/arrays last-writer-wins by completion order (`mergeSequence` in journal), overwrites logged to `state/merge-conflicts.log`. All writes atomic (temp + move).

**Child-process env contract** (absolute paths): `GUARDRAILS_PLAN_DIR`, `GUARDRAILS_TASK_ID/_DIR`, `GUARDRAILS_ATTEMPT`, `GUARDRAILS_STATE_IN` (per-attempt snapshot copy), `GUARDRAILS_STATE_OUT` (actions), `GUARDRAILS_LOG_DIR`, `GUARDRAILS_FEEDBACK` (attempt ≥ 2), `GUARDRAILS_ACTION_STDOUT/_STDERR/_RESULT` (guardrails), `GUARDRAILS_VERDICT_OUT` (prompt guardrails). cwd = workspace (the repo being operated on). Args via `ProcessStartInfo.ArgumentList` (no shell quoting).

## Harness design (C#)

- **TFM**: `net8.0` single-target + `<RollForward>LatestMajor</RollForward>` (runs on 8/9/10 runtimes — widest install base). Deps: `System.CommandLine`, `Spectre.Console` (CLI only, behind `IProgressSink`), `YamlDotNet` (frontmatter), xunit.v3. `System.Text.Json` with comments+trailing-commas allowed (humans hand-edit manifests). No JSON-schema lib — typed records + `PlanValidator` with precise diagnostics.
- **Core types**: `PlanDefinition`/`TaskNode`/`ActionDefinition`/`GuardrailDefinition` (immutable records); `IActionRunner`/`IGuardrailRunner`/`IPromptRunner` + `PromptRunnerRegistry` (the pluggability seam — config carries data, runner class carries flag-spelling/output-parsing code); `ProcessRunner` (interpreter map, tee to logs, timeout, `Kill(entireProcessTree: true)`); `StateManager` (single writer; snapshot/merge); `RunJournal` (`state/run.json`, atomic transitions, `planHash` warns if manifests changed mid-resume); `Scheduler`; `RetryPolicy` (feedback composition).
- **Scheduler**: Kahn-style in-degree counting + `Channel<TaskNode>` ready queue, `maxParallelism` worker loops. `exclusive` tasks acquire the workspace semaphore in full. Cycle detection up front with the cycle path printed.
- **Task lifecycle**: snapshot state → compose env (+ `feedback.md` if attempt ≥ 2) → run action (failed action skips guardrails, counts as failed attempt) → run guardrails (failFast default) → all pass: merge fragment, journal `Succeeded`, unlock dependents → any fail: compose feedback (deterministic: exit code + stderr tail; prompt: verdict reason), retry; budget exhausted → `NeedsHuman`, transitive dependents → `Blocked`, **independent branches continue** (durable progress; surfaces all failures in one pass).
- **Prompt invocation** (`ClaudePromptRunner`): `claude -p --output-format stream-json --verbose --permission-mode acceptEdits --allowedTools ... --max-turns N`, prompt via **stdin**, **cwd = workspace**, `--add-dir <planDir>`. PromptComposer appends to the prompt body: inlined state (≤16 KB else path), output contract (write fragment to STATE_OUT), retry feedback ("fix these specific problems; do not start over"), or the verifier verdict contract ("you do NOT fix anything"). stream-json teed to logs; per-attempt cost logged. Guardrail prompts use the tighter `guardrailOverrides` profile. Never default `bypassPermissions`.
- **CLI**: `guardrails run <folder>` (resume-aware; `--fresh`), `validate` (schema, DAG, file refs, interpreter/runner probes), `plan` (execution waves), `status`, `reset <folder> [task]`. Live Spectre tree progress; plain fallback when redirected. Exit codes: 0 ok / 2 needs-human / 3 cancelled / 1 error. Ctrl+C: cancel token → kill child trees → journal flush → summary; second press hard-exits.
- **Resume**: `Succeeded` skipped; `NeedsHuman`/`Failed`/`Blocked` → `Pending` with fresh budget; `Running` (crash) → `Pending`, attempt numbering continues.

## The `.claude/` ecosystem

### `plan-breakdown` skill (core deliverable)
`SKILL.md` + `references/guardrail-catalogue.md` (archetypes + decision tree), `references/schemas.md` (excerpt citing docs/plans/02), `references/example-breakdown.md` (full worked few-shot + a negative example). Procedure:
0. Preconditions: plan exists; output folder doesn't (else ask overwrite/merge/abort); plan is reviewed; `guardrails` CLI on PATH (else warn validation skipped).
1. Parse plan → scratch table (item | deliverable | completion evidence | hinted deps); flag non-executable content.
2. Size tasks: one verifiable outcome; **split where verification changes character**; one agent-session each; retry-cheap. Heuristic 5–15 tasks, self-report outside it.
3. DAG: edges only from artifact deps, guardrail deps, or explicit ordering — sparsest correct DAG; justification recorded per edge.
4. Guardrails per task (1–4, cheapest-first), **leaning deterministic**: file-exists/contains → command-exit-code → build-passes → specific-tests-pass (`--filter`, never whole-suite early) → lint → schema-validates → port-answers → tests-fail-on-current-code → prompt-judge (last resort, 4-question demotion gate, never alone). Every guardrail file opens with a `# catches: <what wrong implementation this catches>` comment — can't write it, delete the guardrail.
5. **Insert guardrail-enabling tasks**: e.g. guardrail "tests X pass" + tests don't exist → insert upstream `author-tests-X` task whose own guardrails include **tests-fail-on-current-code** (anti-tautology). Rule: a guardrail may only reference artifacts produced by an ancestor task or pre-existing.
6. Write folder; prompt actions embed the verbatim harness-contract block (STATE_IN/STATE_OUT/FEEDBACK/needsHuman escape).
7. Self-validate via `guardrails validate`; emit breakdown report (task table, inserted tasks + justifications, unmapped plan content); close with "**this is a draft — review, edit, then run /guardrail-review**".

### `guardrail-review` skill
Read-only adversarial pass on a generated/human-edited folder: per task, "what's the cheapest wrong implementation that passes ALL these guardrails?" Probes: tautologies, echo-judges, prompt-judge-where-deterministic-possible, over-broad tests, artifacts no ancestor produces, missing tests-fail-on-stub, action-criteria > guardrail-coverage. Plus DAG soundness (missing/false edges, terminal aggregator task) and state-contract lint (consumed keys are produced; failure messages actionable). Findings table with BLOCKER/WEAK/NIT severities; applies fixes only per-finding on approval.

### Agents (house style of `maltby-architect.md`: Role / Skills table / Operating Contract / Deliverable Format / Quality Bar)
| Agent | Owns |
|---|---|
| `guardrails-architect` | `docs/plans/`, schema/contract invariants; designs, never code |
| `guardrails-harness-developer` | `src/**`; refs global developer-standards/coding-standards/dotnet-build-and-test |
| `guardrails-skill-author` | `.claude/skills/**`, `examples/**`; keeps skills in sync with schema changes |
| `guardrails-test-author` | `tests/**` incl. golden-folder meta-tests; refs qa-standards/testing-gate |
| `guardrails-devils-advocate` | findings only — guardrail-gameability focus, wraps global devils-advocate |

### Knowledge skills + uber-report
- `guardrails-domain-knowledge`: the task/guardrail/state model, env contract, retry semantics; cites docs/plans/02 as truth; SELF-UPDATING clause.
- `guardrails-dev-knowledge`: solution layout, tool packaging, run-from-source (`dotnet run --project src/Guardrails.Cli`), test conventions, dogfooding safety (tool-under-test never the installed copy executing the plan).
- `uber-report`: lightweight (~120 lines). Reality Gate booleans: (1) build+tests pass; (2) `guardrails run examples/hello-guardrails/hello-guardrails` completes end-to-end; (3) `plan-breakdown` output passes `guardrails validate`. Until all three: verdict capped at "walking skeleton not proven". Reports → `.claude/tasks/`.

### `examples/hello-guardrails/` (hand-built golden fixture)
3 tasks: `01-write-greeting-script` (script action; file-exists + script-runs-clean guardrails) → `02-generate-greeting` (prompt action; reads `recipientName` from state, writes greeting + fragment; file-exists + file-contains guardrails) → `03-quality-check` (prompt action; report-exists + one deliberate prompt-judge). Exercises both action kinds, state passing, the verdict contract, and a 2-deep chain. Doubles as the harness acceptance fixture and the skill's golden reference.

## Build order (each milestone has an exit criterion)

1. **Foundations**: commit this plan as `docs/plans/00-initial-plan.md` (with a `.gitignore` that keeps `docs/`, `.claude/`, `examples/` tracked); then `docs/plans/00,02,03` (schemas first — everything cites them); `guardrails-domain-knowledge` + `guardrails-architect` authored alongside; hand-build `examples/hello-guardrails/`. *Exit: plan committed + schemas concrete enough that the example folder is fully spec'd.*
2. **Walking skeleton** (script-only, serial): solution + tool packaging; PlanLoader/PlanValidator; ProcessRunner + InterpreterMap; serial executor, exit-code guardrails, no retry; `run` + `validate`. *Exit: a 2-task script plan runs end-to-end on Windows + Linux (CI matrix from day one: windows/ubuntu/macos-latest).*
3. **State + journal + resume**: StateManager, env contract, RunJournal, resume semantics, `status`/`reset`, per-attempt logs. *Exit: kill mid-run, resume, completed tasks skipped.*
4. **DAG + parallelism + retry**: Channel scheduler, cycles, `exclusive`, retry+feedback, needs-human/blocked, Ctrl+C tree-kill, `plan` command, Spectre live UI. *Exit: 4-task diamond fixture — break a guardrail → exit 2 + blocked dependents; fix → resume green. hello-guardrails tasks 01 runs green.*
5. **Prompts**: PromptComposer, frontmatter, IPromptRunner/registry/ClaudePromptRunner, verdict contract, feedback injection; fake-CLI integration tests + one opt-in real-claude smoke test. *Exit: hello-guardrails runs fully green including prompt tasks (Reality Gate 1+2).*
6. **Skills**: `plan-breakdown` (+references) — golden test: regenerates a validate-clean, structurally equivalent folder from `hello-guardrails.md` (Reality Gate 3); then `guardrail-review` pointed first at plan-breakdown's own output; remaining agents + `guardrails-dev-knowledge` + `uber-report`; README.
7. **Polish + dogfood**: validation depth, cost reporting, `--fresh`/`--dry-run`, NuGet publish pipeline; first dogfooded phase plan (roadmap exit criterion: a phase's own plan, broken down + reviewed, executes green under the prior phase's harness).

## v2 bets (named in `docs/plans/03-roadmap.md` from day one; NOT v1 scope)

The four items that turn "useful for one agentic engineer" into "adopted by teams." Each slots into an existing v1 seam — none invalidates the architecture:

1. **Worktree-per-task parallelism** — git worktree per task with merge-on-success replaces the v1 `exclusive`-by-default compromise; unlocks genuinely concurrent prompt tasks on one repo. Seam: Scheduler/workspace resolution (workspace becomes per-task; merge step on `Succeeded`).
2. **CI mode** — `guardrails run --ci` inside GitHub Actions emitting check runs / PR-per-task; journal published as a shared artifact. Seam: `IProgressSink` implementation + exit-code consumer; no core changes.
3. **Executable guardrail template library** — the catalogue graduates from docs to parameterized templates (`tests-pass --filter X`, `port-answers 8080 /health`) that `plan-breakdown` instantiates instead of authoring scripts from scratch; raises guardrail quality, cuts generation variance. Seam: a `templates/` dir + catalogue references.
4. **Cost caps** — per-task/per-run token budget ceilings that trip needs-human instead of spending unattended (per-attempt cost is already logged in v1). Seam: RetryPolicy/TaskExecutor budget check.

## Verification

- Unit: scheduler ordering/parallelism-ceiling/blocked-closure via TCS-gated fake executors; table-driven JsonMerger; retry/feedback scripting; loader diagnostics on bad fixtures; journal resume matrix; ClaudePromptRunner parsing from canned stream-json fixtures.
- Integration: fixture plans with real `.ps1`+`.sh` scripts (picked per OS); end-to-end diamond run asserting state.json/journal/logs/exit codes; timeout kills grandchild processes; fake claude CLI proves the prompt pipeline tokenlessly.
- 3-OS GitHub Actions matrix on every push, from milestone 2 onward.
- Final acceptance: `dotnet tool install -g --add-source ./nupkg guardrails` then `guardrails run examples/hello-guardrails/hello-guardrails` green on a clean machine; `/plan-breakdown` on `hello-guardrails.md` round-trips validate-clean.

## Top risks (carried into uber-report's seeded risk register)

1. Claude CLI contract instability — quarantined in ClaudePromptRunner; verdict files not exit codes; max-turns/timeout cost breakers.
2. Parallel tasks sharing one workspace — prompt actions exclusive-by-default; honest docs; per-resource locks = v2.
3. Plausible-but-weak generated guardrails — "catches:" comments, demotion gate, tests-fail-on-stub, human review, guardrail-review pass, dogfooding as detector.
4. Retry divergence (attempt N builds on attempt N−1's wreckage) — low default retries (2), "fix don't restart" feedback, full attempt logs; auto-clean hooks deferred.
5. Schema drift across docs/C#/skills — single SSOT doc, validate as the skill's exit gate, golden round-trip test in CI, SELF-UPDATING clauses.
