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

The bets that turn "useful for one agentic engineer" into "adopted by teams".
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
5. **E2E web-UI verification** — drive a served UI through its multi-step flow in a
   headless browser and assert the terminal observable (#78), closing the gap above
   #64 (exe starts and serves) and #66 (UI built and served) — which both stop at a
   single request. **Explicitly deferred from v1: the external browser-driver
   dependency (Playwright/Cypress, `playwright install`) and the flakiest guardrail
   archetype are not v1 scope.** Until it lands, an absent E2E driver is surfaced
   (report + honest-halt), never silently scaffolded. *Seam: `$e2eStack` detection in
   `plan-breakdown` Step 0 + a new `interaction-flow` guardrail archetype +
   `references/e2e/<driver>.md`.*
6. **Overwatcher auto-heal + inter-wave adjustment (#269 / #254 v2).** The overwatcher (shipped v1 =
   diagnose + propose; post-v1 fast-follows) graduates to (a) an **`auto` tier** that applies its bounded
   action/budget fixes without prompting — the grant machinery + cumulative retry ceiling already exist
   behind the non-interactive `IOverwatchInteraction` seam, so this bet is the live-TTY-during-live-region
   apply UX (the #145 live-region hazard) plus the persistent authoring-defect fix classes — and (b)
   **overwatcher-driven between-wave adjustment**: on a wave's completion, propose/apply changes to
   downstream (all-`pending`) waves, gated by the same `autonomyPolicy`. The asymmetry is preserved (never
   softens a deterministic verdict; a downstream wave is (re)authored through `/plan-breakdown` +
   `/guardrails-review`, not hand-softened). *Seam: the `IOverwatchInteraction` seam + the between-wave step
   the wave loop already exposes.*

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

## Post-v1 fast-follows

- **#274 definition-drift (staged).** **Part A — halt-always drift fix (rich report): SHIPPED.** A per-task
  `TaskDefinitionHash` (SSOT §7.2) stamped at each successful settle and recomputed on resume; an edit to an
  already-`succeeded` task's definition now HALTS (exit 2) with an itemized `RunReport.DefinitionDrift` —
  old→new hash, best-effort per-file added/removed/modified breakdown, reference `git diff`, and the
  transitive-descendant set — instead of silently reusing the stale segment. **Part C — the safety-check +
  destructive plan-branch rewind primitive: SHIPPED.** One pure, matrix-tested predicate
  (`SafeSuffixEvaluator`) decides whether the drifted set ∪ its descendants forms a provably-safe trailing
  suffix of the plan branch's `--first-parent` trailer history (honoring the merge-tip caveat: a fan-in
  whose merged-in upstreams aren't in the set is refused); when safe, the plan branch is physically rewound
  (`git reset --hard`, reflog-recoverable) past the stale commits and the set journal-reset to re-run.
  **Two consumers:** run-time auto-resolve (gated by the unified `autonomyPolicy` — `"prompt"` default
  prompts interactively / halts in CI, `"auto"` via `--autonomy auto` or the legacy alias `--reprocess-drift`
  auto-resolves, `"halt"` strict) and the manual scoped `reset <folder> <taskId>...` (rewinds a safe set,
  refuses an unsafe one). **Unsafe drift ALWAYS halts — no flag authorizes an unsound rewind**
  (auto-invalidating a fan-in descendant off a stale-carrying base is the unsoundness Part A halted on).
  Auto-resolved runs return the normal exit code + a `boundary:"drift"` entry in the unified `decisions[]`
  audit (M1 fold, SSOT §2.1); only a declined/refused drift is the exit-2 `DefinitionDrift` halt.
- **#254 first-class multi-wave plans — SHIPPED (v1).** Nested `<plan>/<wave>/<tasks>` layout (wave-dir
  regex, contiguous NN; GR2032 mixed / GR2033 numbering / GR2034 cross-wave `dependsOn`); strict-order wave
  execution with a hard barrier + per-wave entry preflight / exit terminal gate; **wave-qualified identity**
  (the §6.2 single-writer state key becomes `<waveDir>/<taskFolder>`); `WaveDefinitionHash` (folds the
  constituent task hashes) + wave-drift resolution via the **marker-aware `SafeSuffixEvaluator`** (exempts
  the empty `Guardrails-Wave:` marker commits, still refuses a trailer-less human hand-fix in range);
  continuous plan branch + cross-wave resume; per-wave task table + diagram; `guardrails reset <plan>
  <wave>`. Authoring: `plan-breakdown` waved output + JIT staged breakdown (author wave N+1 against the
  materialized upstream), `guardrails-review` per-wave. SSOT §14; design `docs/plans/10-multi-wave-plans.md`.
  (M1 foundation + M2a/M2b/M4.) *Recursive model: task ⊂ wave ⊂ plan, one `isCompleted?` predicate separating
  drift from sanctioned forward adjustment. v2 = overwatcher-driven inter-wave adjustment (bet #6).*
- **#269 overwatcher — SHIPPED (v1: diagnose + propose).** An active, tiered, asymmetric run supervisor that
  subsumes the §9.2 needs-human triage: on a struggling task (eager — attempt ≥ 2 + typed transitions,
  once-per-attempt, `maxCostUsd`-bounded) it classifies doomed-vs-retryable + renders a precise diagnosis,
  and (propose tier) offers a bounded action/budget fix. The **asymmetry** (never softens a deterministic
  verdict) is enforced by a `TaskDefinitionFiles`-keyed allow/deny classifier + a triple barrier; tiers map
  onto the unified `autonomyPolicy`; reporting via `decisions[]` (`boundary:"task"`) + a per-task
  `overwatch.jsonl`. SSOT §9.2; design `docs/plans/11-overwatcher.md`. (M3.) *v2 = auto-heal + inter-wave
  adjustment (bet #6).*
- **M1 unified `autonomyPolicy` + `decisions[]` — SHIPPED.** One `prompt`/`halt`/`auto` policy + one
  `boundary: task|wave|drift` decisions log govern drift-resolution (#274), the overwatcher (#269), and
  inter-wave adjustment (#254); folded in the former `driftPolicy`. Prompt-spend from the overwatch diagnose,
  the AI-merge worker, and terminal triage is charged to `maxCostUsd` via the shared `overheadCostUsd` sink
  (#314). SSOT §2.1/§7.
