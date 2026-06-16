---
name: guardrails-domain-knowledge
description: |
  Guardrails product knowledge for all agents working in this repo. Use when working
  on anything related to Guardrails:
  - The task/guardrail/state conceptual model and the four-stage workflow
  - Plan-folder layout, schemas, or child-process contracts
  - Harness execution semantics (retry, needs-human, resume, merge)
  - Authoring or reviewing the plan-breakdown / guardrails-review skills
  - Roadmap, v2 bets, or scope questions

  Provides: the mental model, execution semantics, contract quick-reference, and
  pointers to the single-source-of-truth documents.

  SELF-UPDATING: When your work changes the domain model, schemas, contracts,
  execution semantics, or roadmap in ways that affect this knowledge, you MUST update
  this skill (and docs/plans/02-schemas-and-contracts.md if a contract moved) before
  completing your task. Update the affected section(s) only.
---

# Guardrails Domain Knowledge

## Quick Reference

**What is Guardrails?** A system that turns a reviewed markdown plan into an
executable, file-system task DAG where every task carries executable acceptance
checks ("guardrails"), plus a cross-platform .NET harness (`guardrails` dotnet tool)
that runs the DAG to green — retrying failed tasks with guardrail feedback, and
halting honestly (`needs-human`) when a task can't converge.

**The bet:** in agentic engineering, verification is the bottleneck, not generation.
Humans review the *checks* once instead of reviewing *every agent output* forever.

## The model

- **Plan folder** `<plan-name>/` generated next to `<plan-name>.md`:
  `guardrails.json` (run config) + `state/` (seed, merged state, journal, logs) +
  `tasks/<NN-verb-object>/` (one folder per task) + an optional generated
  `diagram.md` (see below).
- **Diagram** — two companion files written by `guardrails graph` at the plan-folder root,
  both generated, non-authored, and excluded from `guardrails.baseline`:
  - `diagram.md`: Mermaid `flowchart TD` (GitHub render artifact). First line is a
    `<!-- guardrails:graph v1 source-sha256=<hash> -->` provenance comment. NOT part of the
    plan contract; safe to delete and regenerate.
  - `diagram.html`: interactive local-navigation companion (pan/zoom/fullscreen + Mermaid
    `click href` directives pointing to task/guardrail source). Suppressed by `--no-html`.
    Node clicks require a local HTTP server — browsers block `file://→file://` by default.
  Both share the same `source-sha256` key. `guardrails graph --check` exits 0 (fresh), 2
  (stale/missing — a present but hash-mismatched `diagram.html` is stale; a missing one is
  not), 1 (load/validate error). See SSOT §10.
- **Task** = `task.json` (`description`, `dependsOn`, optional `retries`/`timeoutSeconds`/
  `exclusive`/`action`) + one action file + `guardrails/` with ≥1 guardrail.
  Zero guardrails = validation error.
- **Action kinds**: `.prompt.md` → LLM (via pluggable `IPromptRunner`; v1 = Claude Code
  CLI headless); anything else → process via the interpreter map.
- **Guardrails**: deterministic (exit 0 = pass; failure reason on stdout) or prompt
  (MUST write `{pass, reason}` verdict JSON to `GUARDRAILS_VERDICT_OUT` — CLI exit
  codes are never semantic). Ordered by filename, cheapest first. ALL must pass.
  Every guardrail opens with a `catches:` comment naming the wrong implementation
  it would catch.
- **State**: snapshot-in / fragment-out. Attempt gets an immutable snapshot
  (`GUARDRAILS_STATE_IN`); action may write a JSON-object fragment
  (`GUARDRAILS_STATE_OUT`); harness (single writer) deep-merges fragments into
  `state/state.json` in completion order after guardrails pass. Scalars/arrays
  last-writer-wins; overwrites logged. `state/seed.json` (committed) seeds the
  runtime state.

## Execution semantics

- Attempt = snapshot → run action (failed action skips guardrails) → run guardrails
  (`failFast` default) → all pass: merge fragment + `succeeded` → else compose
  `feedback.md` and retry (prompt actions get the feedback injected: "fix these
  specific problems; do not start over").
- Retry budget exhausted → `needs-human`; transitive dependents → `blocked`;
  **independent branches keep running**.
- **Per-run cost cap** (`maxCostUsd` in `guardrails.json`, optional decimal USD): when
  the journal's cumulative cost (sum of each attempt's `costUsd`) reaches/exceeds the
  cap, the scheduler stops launching new attempts — each not-yet-launched task settles
  `needs-human` ("cost cap reached") and its transitive dependents `blocked` (the same
  halt path above); an in-flight attempt is never interrupted. Absent ⇒ no cap; a
  non-positive cap is a validation error (GR2012). See SSOT §2.
- Prompt actions default `exclusive: true` (sole workspace access) — two agents
  editing one repo concurrently is the #1 real-world failure mode. Deterministic
  actions default shared.
- Resume: `succeeded` is terminal (use `guardrails reset` to force);
  `needs-human`/`failed`/`blocked` → fresh budget; crashed `running` → `pending`.
- Harness exit codes: 0 green · 1 error · 2 needs-human · 3 cancelled.
- A prompt action can short-circuit with `{ "needsHuman": "<question>" }` in its
  fragment — no retry burn on a genuine human decision.

## The four-stage workflow

PLAN (human+agents write/review the .md) → BREAK (`/plan-breakdown` generates the
folder, **inserting guardrail-enabling tasks** like authoring the unit tests an
implementation task's guardrails will run — with tests-fail-on-current-code proving
non-tautology) → REVIEW (human edits; `/guardrails-review` adversarial pass: "what
wrong implementation passes these?") → RUN (`guardrails run`). Generated output is
always a **draft** until a human reviews it.

BREAK ends by generating `diagram.md` (`guardrails graph`); REVIEW re-checks it
(`guardrails graph --check`) and regenerates if the human's edits made it stale —
the diagram is a deterministic projection that must track the folder, not a snapshot
frozen at breakdown.

## Load-bearing invariants

1. **Deterministic over prompts** — prompt-judges are last resort, never alone, and
   must pass the demotion gate in the guardrail catalogue.
2. **Harness is the single writer of merged state**; children only ever get
   snapshots and write fragments.
3. **Verdicts come from files, never CLI exit codes** (prompt guardrails).
4. **`docs/plans/02-schemas-and-contracts.md` is the schema SSOT** — C# serializers,
   skills, and examples implement it; never fork it.
5. **Honest halts** — the harness never marks unverified work done.

## Where truth lives

| Question | Document |
|---|---|
| Any schema / env var / contract | `docs/plans/02-schemas-and-contracts.md` (SSOT) |
| Mental model, principles, out-of-scope | `docs/plans/01-overview.md` |
| Milestones, exit criteria, v2 bets, risk register | `docs/plans/03-roadmap.md` |
| Founding plan (history) | `docs/plans/00-initial-plan.md` |
| Golden example (runnable + skill reference) | `examples/hello-guardrails/` |

## Status (update as milestones complete)

- M1 Foundations: **complete** (docs + golden example committed).
- M2 Walking skeleton: **complete**. Solution (`Guardrails.Core`/`.Cli`, net8.0 dotnet
  tool), `PlanLoader`/`PlanValidator` with precise diagnostic codes (GR10xx loading,
  GR20xx validation), `InterpreterMap` (SSOT §5.2, injectable PATH probe),
  `ProcessRunner` (ArgumentList, tree-kill timeout), `SerialExecutor` (serial, ordinal
  folder order, dependency-failure → blocked, no retry), and `guardrails run`/`validate`.
  3-OS CI matrix in `.github/workflows/ci.yml`. M2 runs **script actions only** — a plan
  with prompt actions/guardrails validates fine but `run` fails fast ("not supported
  until M5"). State/journal/log env vars are **not** set yet (only `GUARDRAILS_PLAN_DIR`,
  `_TASK_ID`, `_TASK_DIR`, `_ATTEMPT`="1").
- M3 State + journal + resume: **complete**. `JsonMerger` (pure deep-merge: objects
  recurse, scalars/arrays last-writer-wins, conflicts reported) + `StateManager` (single
  writer of `state/state.json`: seed/init, immutable `state-in.json` snapshots, fragment
  merge after guardrails pass, `merge-conflicts.log`, atomic writes; invalid non-object
  fragment → distinct `invalid-fragment` outcome, state unchanged). `RunJournal`
  (`state/run.json` per §7: kebab-case status/outcome strings, attempt records, `planHash`
  = SHA-256 over guardrails.json + all task.json, loud warning on resume mismatch) with the
  §7 resume matrix — `succeeded` skipped, `needs-human`/`failed`/`blocked` → `pending`,
  crashed `running` → `pending` with attempt numbering continuing. `SerialExecutor` now
  threads snapshot → action (tee stdout/stderr + `action-result.json`) → guardrails
  (with `GUARDRAILS_ACTION_*`/`STATE_FRAGMENT`) → merge, writes the §8 per-attempt log
  layout, and skips journal-`succeeded` tasks on resume. Env contract §5.1 completed for
  scripts (`STATE_IN`/`STATE_OUT`/`STATE_FRAGMENT`/`LOG_DIR`/`ACTION_STDOUT`/`_STDERR`/
  `_RESULT`; `FEEDBACK` is M4). New CLI: `status` (read-only journal table), `reset
  <folder> [task]`, and `run --fresh`. M4 still owns DAG/parallelism/retry/needs-human.
- M4 DAG + parallelism + retry: **complete**. `DependencyGraph` (cycle detection →
  GR2007, waves, transitive-dependent closure), Channel-based `Scheduler`
  (maxParallelism workers; failure blocks the transitive closure while independent
  branches finish; resume pre-pass), `TaskExecutor` retry loop (budget = 1 + retries;
  `feedback.md` written per failed attempt and delivered via `GUARDRAILS_FEEDBACK`
  from attempt 2; budget exhaustion → `needs-human`; cancellation → attempt outcome
  `cancelled`, task journaled `pending`), FIFO shared/exclusive `WorkspaceLock`
  (prompt actions exclusive by default per §3), `guardrails plan` (waves preview),
  Spectre live table UI (plain-line fallback when non-interactive or `--no-ui`),
  exit code 3 on cancellation. `SerialExecutor` is gone — `Scheduler` with
  maxParallelism 1 is serial mode; test fixtures pin `defaultRetries: 0` to keep
  single-attempt assertions exact.
- M5 Prompts: **complete**. Full `promptRunners` config parsing (`PromptRunnerConfig`
  with base `PromptRunnerSettings` + a partial `guardrailOverrides` block merged for
  guardrail prompts; `command`/`permissionMode`/`allowedTools`/`maxTurns`/`model`/
  `extraArgs`); a plan with prompt tasks but no `promptRunners` config is a validation
  error (**GR2008**). YAML frontmatter (`PromptFileParser`, YamlDotNet) for `.prompt.md`
  actions and guardrails — `description`/`runner`/`maxTurns`/`timeoutSeconds`, optional,
  body stripped; malformed frontmatter is a loading error. Prompt pipeline under
  `Prompts/`: `IPromptRunner` seam, `PromptInvocation`/`PromptResult`,
  `PromptRunnerRegistry` (name → runner, default resolution), `ClaudePromptRunner` (the
  ONLY place Claude flag spelling + `stream-json` parsing live — prompt via **stdin**,
  cwd = workspace, `--add-dir <planDir>`, tolerant line-wise parse teed to
  `claude-stream.jsonl`; non-zero exit OR no terminal `result` ⇒ not completed),
  `PromptComposer` (`composed-prompt.md` = body + Shared state [inlined ≤ 16 KB else by
  path] + Output contract/needsHuman [actions] + Previous-attempt feedback [actions,
  attempt ≥ 2] + Verdict contract [guardrails]), `GuardrailVerdictReader` (verdict file
  is the ONLY pass/fail authority; missing/invalid/missing-`pass` ⇒ fail "guardrail
  produced no valid verdict (see logs)"). `TaskExecutor` routes `ActionKind.Prompt`:
  action success = completed AND `is_error == false`; per-attempt `costUsd` recorded;
  prompt guardrails set `GUARDRAILS_VERDICT_OUT` and use the `guardrailOverrides` profile.
  **needsHuman short-circuit** (SSOT §9): a prompt action whose fragment has a root string
  `needsHuman` key escalates IMMEDIATELY — new outcome `needs-human` (kebab-case;
  `AttemptOutcome.NeedsHuman` / `TaskOutcome.NeedsHuman`), no retry burn, no guardrails,
  no fragment merge. `Scheduler.EnsureNoPrompts` and `PromptNotSupportedException` removed
  — prompts now run. Tested tokenlessly by a fake-CLI (`.cmd`→`.ps1` on Windows, `.sh`
  elsewhere) across all four scenarios + an opt-in real-claude smoke test
  (`GUARDRAILS_REAL_CLAUDE=1`).
- **Reality Gate 1 + 2: MET.** `guardrails run examples/hello-guardrails/hello-guardrails`
  completes end-to-end green with the real Claude CLI — script action, two prompt actions,
  and the prompt verdict-contract guardrail all pass; costs journaled (~$1.00 total).
- M6 Skills: **complete**. `plan-breakdown` SKILL.md + references (guardrail-catalogue
  with the archetype table / decision tree / demotion gate / anti-patterns; schemas
  excerpt citing the SSOT; full worked example incl. an inserted test-author task and
  a negative example), `guardrails-review` (adversarial "cheapest wrong implementation"
  pass, BLOCKER/WEAK/NIT, per-finding approval), agents `guardrails-harness-developer`/
  `-skill-author`/`-test-author`/`-devils-advocate`, `guardrails-dev-knowledge`,
  lightweight `uber-report` (Reality Gate-first), and the real README (workflow,
  60-second demo, CLI table). **Round-trip proven**: a fresh breakdown of
  `hello-guardrails.md` following the skill procedure validates clean (exit 0) and
  matches the golden folder's structure (same task split, same guardrail archetypes).
- **Reality Gate: MET — all three booleans** (build+tests; end-to-end example run on
  real Claude, verified 2026-06-10 at ~$0.90; plan-breakdown round-trip).
- M7 Polish + packaging: **complete except dogfood execution** (which awaits human
  review — that is the workflow). Run-level cost aggregation: `JournalCost.Total` sums
  every attempt's `costUsd`; the `run` summary and `status` print `Total prompt cost:
  $X.XXXX`, omitted when no attempt recorded a cost (deterministic plans stay quiet).
  `guardrails run --dry-run`: validates, prints the waves preview + per-task resolution
  (kind/runner/exclusive/retry-budget) + journal-aware resume SKIPs, then exits 0 having
  run nothing and touched no state (reads `run.json` read-only — no `LoadOrCreate`).
  Validation depth: `validate` probes each DECLARED prompt runner's `command` on PATH as a
  **warning** (GR2009, not an error — the plan may run elsewhere). Packaging: PackageId
  `ServantSoftware.Guardrails` (ToolCommandName stays `guardrails`), version
  `1.0.0-preview.1`, MIT `LICENSE` + full package metadata, README packed; release pipeline
  `.github/workflows/release.yml` (tag `v*` → 3-OS matrix → pack with version derived from the
  tag → `dotnet nuget push` via **Trusted Publishing**/OIDC, no long-lived key — `NuGet/login@v1`
  + a `NUGET_USER` profile-name secret + a nuget.org policy). Clean-machine pack/install
  acceptance passed (validate + plan via the installed tool, then uninstalled). **Dogfood artifact authored, not executed**:
  `docs/plans/04-dogfood-cost-cap.md` + its breakdown folder (validate-clean, 4 tasks:
  inserted test-author → implement → document → terminal suite-green) await human review
  before `guardrails run` — the first real v2 slice (per-run cost cap).
