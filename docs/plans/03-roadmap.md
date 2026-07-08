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
6. **Overwatcher-driven self-healing + inter-wave adjustment** (#269, #254 v2) — an AI
   run supervisor that, at a decision boundary, grants adjusted attempts / diagnoses a
   doomed task / applies a bounded authoring fix (never softening a deterministic
   guardrail's verdict), AND — between waves of a multi-wave plan — proposes intelligent
   adjustments to the next (all-pending) wave. Gated by the shared `autonomyPolicy`
   (prompt/halt/auto, SSOT §2.1) and reported in the shared `boundary`-discriminated
   decisions log. **#269 design of record: `11-overwatcher.md`** — its v1 *diagnose +
   propose* supervisor is a post-v1 fast-follow (below); this bet is the v2 slice
   (`auto`-value silent auto-heal + the inter-wave role). *Seam: the multi-wave
   skeleton's between-wave decision point + the shared autonomy policy + decisions log,
   all defined by the #254 v1 skeleton (below).*

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
  destructive plan-branch rewind primitive, exposed as manual scoped `reset <folder> <taskId>` AND opt-in
  run-time auto-resolve of the safe (trailing-suffix) set: DEFERRED to its own design→draft-PR review**
  (destructive/load-bearing — auto-invalidating a fan-in descendant is unsound, which is why Part A halts).

- **#254 multi-wave plans — v1 wave-execution SKELETON** (design of record `10-multi-wave-plans.md`). Nested
  `<plan>/<wave>/tasks/…` layout with strict-order wave execution: a thin wave loop + hard barrier above the
  existing Scheduler; per-wave entry/exit gates (the four-folder model gains a middle **wave scope**);
  wave-qualified task identity (journal key / `Guardrails-Task:` trailer / state single-writer key);
  a continuous plan branch + cross-wave resume; the recursive **`task ⊂ wave ⊂ plan`** completion-unit model
  — durable wave completion via a `Guardrails-Wave:` marker commit + a `WaveDefinitionHash` that nests
  between `PlanDefinitionHash` and `TaskDefinitionHash`, wave-level drift, and wave-scoped `reset`
  (always a safe suffix, since strict order forbids cross-wave fan-in). Between waves in v1 = a plain human
  JIT-breakdown/review checkpoint (honest halt if the next wave isn't ready). Ships the **shared foundation**
  the overwatcher (bet #6) reuses: the unified `autonomyPolicy` (SSOT §2.1) + the `boundary`-discriminated
  decisions log. **Auto-heal + overwatcher-driven inter-wave adjustment are v2 bets (bet #6), not v1** — v1
  only defines the seam.

- **#269 overwatcher — v1 active *diagnose + propose* supervisor** (design of record `11-overwatcher.md`).
  The active generalization of the shipped one-shot §9.2 triage into an in-run supervisor that fires at
  conservative, deterministically-detected struggle transitions (once per transition), classifies
  doomed-vs-retryable, and renders a precise diagnosis under the task table + the shared `decisions[]`
  (`boundary:"task"`). Governed by the shared `autonomyPolicy` (SSOT §2.1): `halt` = diagnose + always
  halt; `prompt` (default) = diagnose + TTY-propose the sanctioned **action-layer** levers (ephemeral
  guidance injection / `maxTurns`·`retries`·`timeout` runtime budget bumps), honest-halt when
  non-interactive. The mechanical asymmetry — a `TaskDefinitionFiles`-keyed classifier the harness (not
  the LLM) applies — keeps the **verdict surface** (guardrail/preflight bodies + `writeScope`/`scope`/
  `dependsOn`/`integrationGate`) propose-only + review-marker-re-staling (via #260), while the
  **action/budget layer** is auto-applicable. Subsumes §9.2 (now its terminal-exhaustion case §9.2.1);
  generalizes #94/#264/#174 as policies while the deterministic short-circuits stay the floor ("no
  sanctioned change ⇒ no grant ⇒ honest halt"). **Bounded auto-heal (`auto`-value silent application +
  authoring-defect fix classes) and the inter-wave role are v2 (bet #6)** — v1 defines the seam. No new
  GR code (reuses GR2031/`autonomyPolicy` + GR2025/review-staleness; next-free is GR2035 if ever needed).
