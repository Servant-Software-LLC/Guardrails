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
that runs the DAG to green -- retrying failed tasks with guardrail feedback, and
halting honestly (`needs-human`) when a task can't converge.

**The bet:** in agentic engineering, verification is the bottleneck, not generation.
Humans review the *checks* once instead of reviewing *every agent output* forever.

## The model

- **Plan folder** `<plan-name>/` generated next to `<plan-name>.md`:
  `guardrails.json` (run config) + `state/` (seed, merged state, journal) +
  `logs/<runId>/<task-id>/attempt-N/` (per-attempt artifacts, sibling of `state/`,
  divided by `runId` so re-runs never interleave) +
  `tasks/<NN-verb-object>/` (one folder per task) + optional `diagram.md`/`diagram.html`.
- **Diagram** -- two companion files written by `guardrails graph` at the plan-folder root,
  both generated, non-authored, and excluded from `guardrails.baseline`:
  - `diagram.md`: Mermaid `flowchart TD` (GitHub render artifact). First line is a
    `<!-- guardrails:graph v1 source-sha256=<hash> -->` provenance comment. NOT part of the
    plan contract; safe to delete and regenerate.
  - `diagram.html`: interactive local-navigation companion (pan/zoom/fullscreen + Mermaid
    `click href` directives pointing to task/guardrail source). Suppressed by `--no-html`.
    Node clicks require a local HTTP server -- browsers block file://->file:// by default.
  Both share the same `source-sha256` key. `guardrails graph --check` exits 0 (fresh), 2
  (stale/missing), 1 (load/validate error). See SSOT section 10.
- **Task** = `task.json` (`description`, `dependsOn`, optional `retries`/`timeoutSeconds`/
  `integrationGate`/`writeScope`/`action`) + one action file + `guardrails/` with >=1 guardrail.
  Zero guardrails = validation error.
  - `integrationGate: true` (optional, default false) marks the terminal whole-repo integration
    gate (SSOT section 3.3). Required for any multi-leaf/fan-in plan (GR2017 error if absent);
    the gate task's guardrails must include >=1 `scope: "integration"` guardrail (GR2018 if not).
  - `writeScope: ["src/Foo/"]` (optional) drives the deterministic **write-scope CHECK** (SSOT
    section 3.4): after the action, before the task's own guardrails, the harness computes
    `git diff --name-status <taskBase>..<HEAD>` in the segment worktree and asserts every changed
    path is in scope. Absent => no check. A violation is a guardrail-class failure (retry with
    feedback naming the out-of-scope paths; eventual `needs-human`). **Never reverts** -- read-only
    check only. Renames = paired D+A (both in scope). GR2019 (error): scope entry escapes
    workspace. GR2020 (warning): vacuous/over-broad scope. TDD triad replacement.
  - **The triad (`captureHashes`/`restoreOnRetry`/`exclusive`) and `WorkspaceLock` are REMOVED**
    -- replaced by physical worktree isolation (one task per segment worktree) and `writeScope`.
- **Worktree isolation**: the workspace must be a git repository top-level (GR2015 error
  otherwise; GR2016 warning for deep `worktreeRoot` + deep source tree risking Windows MAX_PATH).
  At run start the harness creates a **plan branch** `guardrails/<plan-name>` off the user's HEAD
  and an integration worktree on it (sole merge target). Each task runs in a **segment worktree**:
  - Linear chain: **reuses one** segment worktree (downstream commits on top of upstream's tip
    in the same tree -- no inter-hop merge, no inter-hop re-verify).
  - Fan-out: **inherits one** chain, **forks the rest** off the producer's committed tip (W-2,
    not the inheritor's advanced tip).
  - Fan-in: **forks one** upstream and merges the others in (union settle, SSOT section 5.3).
  Worktrees are created under a harness-owned root outside the workspace (default
  `<temp>/guardrails-worktrees/<hash>/<runId>/`, overridable via `worktreeRoot` in
  `guardrails.json`). `maxParallelism` defaults to **3**. The user's checkout is **read-only
  for the entire run**; the only optional write to the user's branch is `--merge-on-success`
  (AI-merge is withheld at that boundary).
- **Action kinds**: `.prompt.md` -> LLM (via pluggable `IPromptRunner`; v1 = Claude Code
  CLI headless); anything else -> process via the interpreter map.
- **Guardrails**: deterministic (exit 0 = pass; failure reason on stdout) or prompt
  (MUST write `{pass, reason}` verdict JSON to `GUARDRAILS_VERDICT_OUT` -- CLI exit
  codes are never semantic). Ordered by filename, cheapest first. ALL must pass.
  Every guardrail opens with a `catches:` comment naming the wrong implementation it would catch.
  **Verify-don't-replay (#62):** a guardrail receives the action's recorded outcome --
  `GUARDRAILS_ACTION_RESULT` (`{kind, exitCode, summary}`), `_STDOUT`, `_STDERR` (SSOT
  section 5.1) -- and may verify a postcondition from it instead of re-running the action's
  command. It's a speed/flake trade-off, sound only against output the action couldn't fabricate.
  **Guardrail scope** (SSOT section 4.3): a guardrail declares an optional `scope` (`"local"`
  default, or `"integration"`). The **integration-guardrail set** = all `scope: "integration"`
  guardrails across the plan (typically the whole-repo build + full test suite). At **every** union
  point (a fan-in or a non-FF plan-branch integration) the harness re-runs, on the merged bytes,
  **the integration set ONLY** -- one set, run uniformly at every union and again on the final merged
  HEAD by the terminal `integrationGate` sink. There is **no** per-task or per-colliding-sibling
  guardrail selection at a union in v1: the integration set IS the whole re-verify. (The earlier
  "union task's own set + each colliding sibling's full set + integration set" three-part model was
  **never implemented** -- `ShouldRunAtUnion` had zero callers; SSOT section 4.3 is integration-set-only.)
  **Accepted residual (#132):** because the union re-verify is integration-set-only, a hunk an AI-merge
  silently drops on a **shared file** (overlapping `writeScope`s of colliding siblings) is re-verified
  at the union ONLY by an integration-scoped guardrail; a drop catchable solely by a sibling's `local`
  guardrail is NOT re-run at the union (running `local` guardrails on union bytes false-fails -- no
  fragment, inverted anti-tautology). The mitigation is **authoring, not runtime**: author a
  `scope:"integration"` union-guardrail on the shared file (`plan-breakdown` emits one for overlapping
  scopes; `guardrails-review` flags its absence WEAK). See SSOT section 4.3 "Accepted residual".
- **State**: snapshot-in / fragment-out. Attempt gets an immutable snapshot
  (`GUARDRAILS_STATE_IN`); action may write a JSON-object fragment (`GUARDRAILS_STATE_OUT`);
  harness (single writer) deep-merges fragments into `state/state.json` in completion order after
  guardrails pass. **Single-writer-per-key is ENFORCED, not a convention (SSOT section 6.2)**:
  a fragment's top-level keys must each equal the writing task's OWN id -- which is its **FOLDER
  NAME** (the directory the `task.json` lives in, e.g. `02-generate-greeting`), NOT its `stableId`
  (an internal regeneration token; a `stableId`-keyed fragment is rejected as foreign, #164)
  (reserved keys -- none in
  v1). A foreign task id or any arbitrary shared key makes the fragment invalid-fragment -- it is
  rejected (not stripped), the attempt fails and retries with feedback, and nothing merges. This
  makes the harness the sole writer of every task's namespace. Scalars/arrays last-writer-wins
  within a task's own namespace. `state/seed.json` (committed) seeds the runtime state.

## Execution semantics

- Attempt = snapshot -> run action (failed action skips guardrails) -> **write-scope check**
  (if `writeScope` set: deterministic read-only git diff membership test in the segment worktree;
  violation = retry with feedback naming out-of-scope paths) -> run guardrails (`failFast` default)
  -> all pass: merge fragment + `succeeded` -> else compose `feedback.md` and retry.
- **Failed-attempt retry**: `git reset --hard <taskBase> + git clean -fd` in the segment worktree
  (preserving every upstream/sibling commit; `taskBase` != `preHead`).
- Retry budget exhausted -> `needs-human`; transitive dependents -> `blocked`;
  **independent branches keep running**.
- **Prompt-runner failure classification** (SSOT section 9, #114/#115/#119): a non-success prompt
  result is classified (in the runner quarantine) into `Transient` | `OutputCap` | `Timeout` | `Error`.
  - **Transient** (429/503/529, "overloaded", rate/session/usage limit): does NOT consume the retry
    budget -- the harness PAUSES (bounded backoff, bounded by `transientPauseBudgetSeconds`) and
    re-runs the same attempt, surfacing a distinct `PromptPaused` observer event. A rate limit is
    NEVER `needs-human` until the pause budget is spent (then a distinct `rate-limited` outcome,
    "re-run later"). A cleared pause is never journaled (observe-only).
  - **OutputCap** (`CLAUDE_CODE_MAX_OUTPUT_TOKENS`, default raised to 64000 via `maxOutputTokens`):
    distinct `output-cap` outcome + actionable "write incrementally / split" retry feedback.
  - **Timeout**: distinct `timeout` outcome + "continue from preserved partial work" feedback, and
    the retry clock is EXTENDED (1x -> 1.5x -> 2.25x, capped 4x).
- **Per-run cost cap** (`maxCostUsd` in `guardrails.json`, optional decimal USD): when
  the journal's cumulative cost reaches/exceeds the cap, the scheduler stops launching new
  attempts -- each not-yet-launched task settles `needs-human` ("cost cap reached") and its
  transitive dependents `blocked`; an in-flight attempt is never interrupted. Absent => no cap;
  non-positive cap is a validation error (GR2012). See SSOT section 2.
- **Integration (A) Fast-forward**: a linear chain's commit where no sibling has advanced the
  plan branch -> `git merge --ff-only`, **no new union, no re-verify** (bytes already passed
  the task's guardrails in the segment worktree). The FF'd commit carries
  `Guardrails-Task`/`Guardrails-Run` trailers for resume.
- **Integration (B) Union** (fan-in, or non-FF where a sibling raced): real merge that MUST be
  re-verified on the merged bytes. Resolution: git auto-merge -> **AI-merge** -> human.
  - `git merge --no-commit`; on conflict, the **AI-merge worker** (a constrained prompt behind
    `IPromptRunner`) is a **BYTE PRODUCER** only -- it writes resolved bytes to
    `GUARDRAILS_MERGE_OUT` via the three-way on-disk env contract (`GUARDRAILS_MERGE_BASE`,
    `GUARDRAILS_MERGE_OURS`, `GUARDRAILS_MERGE_THEIRS`, `GUARDRAILS_MERGE_OUT`). Two
    **deterministic checks** gate the output: (i) no conflict markers remain (`git diff --check`);
    (ii) blast-radius: modified only the git-reported-conflicted files (`git status --porcelain`).
    Violation -> discard (`reset --hard`) + `needs-human`. 1 retry. `PromptResult.IsError` and
    exit code are **never** the verdict. AI-merge resolves harness-internal unions only; withheld
    at the `--merge-on-success` user-branch boundary.
  - **The verdict** (for both clean-auto and AI-resolved) is the **deterministic re-verify**:
    re-run **the integration-guardrail set** (integration-set-only, SSOT section 4.3) on the merged
    bytes via the attempt-decoupled `IReVerifier` seam (no attempt lifecycle, no action result). Any
    fail -> `reset --hard preHead`; `needs-human`; no fragment, no `mergeSequence`.
  - AI-merge + re-verify run in a private forked worktree **off** the serialize lock; only the
    final integration of the verified result into the plan branch is **under the lock**.
- **B1 atomic settle** (under the serialize lock, fixed order): (1) deep-merge fragment into
  `state.json`; (2) `git commit` the integration carrying `Guardrails-Task`/`Guardrails-Run`
  trailers (FF'd commits AND merge commits); (3) consume `mergeSequence` + journal `Succeeded`.
- **Resume-by-trailer**: the plan-branch's trailer-bearing commits are the durable resume record.
  On resume, stale segment refs (`guardrails/<runId>/*`) are deleted **before** any trailer read
  (W-1: a trailer on a surviving segment ref that never FF'd is not authoritative).
- **End-of-run delivery**: when the run drains green AND `mergeOnSuccess`/`--merge-on-success` is
  set, the harness merges the plan branch into the user's original branch. **AI-merge is NOT used
  here.** A conflict / failed re-verify / dirty user tree halts, plan branch intact. Default OFF.
  The outcome is a `MergeOnSuccessResult`: `FastForwarded` / `Merged` (delivered, exit 0) or
  `Conflict` / `DirtyWorkingTree` / `HookRejected` (halted; work is durable on the plan branch, exit 2).
- **Hook policy at the two commit boundaries (#149)** — internal vs user-facing commits are treated
  oppositely:
  - **Internal bookkeeping commits bypass user hooks.** The segment integration commit (`Integrate`,
    `--no-verify --allow-empty`) and the non-FF union merge commit (`CommitStagedMerge`, `--no-verify`)
    run with `--no-verify` — they are machine commits in throwaway worktrees on `guardrails/<plan>`,
    not the user's deliverable, so a global `pre-commit` hook (e.g. GitGuardian `ggshield`) must never
    gate harness plumbing. (Motivating incident: an offline `ggshield` hook failed an internal marker
    commit and crashed a run.)
  - **The user-facing merge-back KEEPS hooks** — deliberately, exactly like a manual `git merge`. The
    non-FF merge commit runs the user's `pre-commit`/`commit-msg` hooks. If a hook rejects it, the
    harness `git merge --abort`s (user branch left clean at its pre-merge HEAD), leaves the plan branch
    intact, surfaces the hook's stderr, and returns `HookRejected` — a graceful delivery-halt, not a
    crash. **Inherent FF caveat (intended):** an FF delivery creates no commit, so no commit hook fires
    there; hooks run on the non-FF merge commit only.
- **Honest halt on infrastructure faults (#150)**: an unexpected fault during a run (a task executor or
  integration git step throwing — e.g. git unavailable) is NOT an unhandled crash. The scheduler returns
  an **aborted `RunReport`** carrying a `RunAbort` (one-line `Headline`/`Remedy` + full exception
  `Detail`); the CLI renders the one-liner + remedy, writes the full fault to `logs/<runId>/abort.log`,
  and exits non-zero (harness error, exit 1). End-of-run cleanup still runs. An aborted report is failed
  regardless of per-task outcomes.
- Resume: `succeeded` is terminal (use `guardrails reset` to force);
  `needs-human`/`failed`/`blocked` -> fresh budget; crashed `running` -> `pending`.
- Harness exit codes: 0 green / 1 harness or validation error (incl. a run **aborted** by an
  infrastructure fault, #150) / 2 needs-human or blocked, OR a wholly-green run whose opt-in delivery
  was **halted** (`Conflict`/`DirtyWorkingTree`/`HookRejected` — work durable on the plan branch) /
  3 cancelled. See SSOT section 7.1.
- A prompt action can short-circuit with `{ "needsHuman": "<question>" }` in its fragment --
  no retry burn on a genuine human decision.

## The four-stage workflow

PLAN (human+agents write/review the .md) -> BREAK (`/plan-breakdown` generates the folder,
**inserting guardrail-enabling tasks** like authoring the unit tests an implementation task's
guardrails will run -- with tests-fail-on-current-code proving non-tautology) -> REVIEW
(human edits; `/guardrails-review` adversarial pass: "what wrong implementation passes these?")
-> RUN (`guardrails run`). Generated output is always a **draft** until a human reviews it.

BREAK ends by generating `diagram.md` (`guardrails graph`); REVIEW re-checks it
(`guardrails graph --check`) and regenerates if the human's edits made it stale.

## Load-bearing invariants

1. **Deterministic over prompts** -- prompt-judges are last resort, never alone, and
   must pass the demotion gate in the guardrail catalogue.
2. **Harness is the single writer of merged state**; children only ever get snapshots and
   write fragments.
3. **Verdicts come from files, never CLI exit codes** (prompt guardrails). The AI-merge
   worker is a BYTE PRODUCER -- its exit code is never a verdict either.
4. **`docs/plans/02-schemas-and-contracts.md` is the schema SSOT** -- C# serializers,
   skills, and examples implement it; never fork it.
5. **Honest halts** -- the harness never marks unverified work done.
6. **Worktree isolation is the concurrency safety boundary** -- each task runs in its own
   segment worktree; the user's checkout is read-only for the entire run.

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
  GR20xx validation), `InterpreterMap` (SSOT section 5.2, injectable PATH probe),
  `ProcessRunner` (ArgumentList, tree-kill timeout), `SerialExecutor` (serial, ordinal
  folder order, dependency-failure -> blocked, no retry), and `guardrails run`/`validate`.
  3-OS CI matrix in `.github/workflows/ci.yml`. M2 runs **script actions only** -- a plan
  with prompt actions/guardrails validates fine but `run` fails fast ("not supported until M5").
  State/journal/log env vars are **not** set yet (only `GUARDRAILS_PLAN_DIR`, `_TASK_ID`,
  `_TASK_DIR`, `_ATTEMPT`="1").
- M3 State + journal + resume: **complete**. `JsonMerger` (pure deep-merge) + `StateManager`
  (single writer of `state/state.json`: seed/init, immutable snapshots, fragment merge after
  guardrails pass, `merge-conflicts.log`, atomic writes). `RunJournal` (`state/run.json` per
  section 7: kebab-case status/outcome strings, attempt records, `planHash`, loud warning on
  resume mismatch) with the section 7 resume matrix. `SerialExecutor` now threads snapshot ->
  action -> guardrails -> merge, writes the section 8 per-attempt log layout. New CLI: `status`,
  `reset <folder> [task]`, `run --fresh`. M4 still owns DAG/parallelism/retry/needs-human.
- M4 DAG + parallelism + retry: **complete**. `DependencyGraph` (cycle detection -> GR2007,
  waves, transitive-dependent closure), Channel-based `Scheduler` (maxParallelism workers;
  failure blocks the transitive closure while independent branches finish; resume pre-pass),
  `TaskExecutor` retry loop (budget = 1 + retries; `feedback.md` written per failed attempt;
  budget exhaustion -> `needs-human`; cancellation -> `pending`), `guardrails plan` (waves
  preview), Spectre live table UI, exit code 3 on cancellation. `SerialExecutor` is gone --
  `Scheduler` with maxParallelism 1 is serial mode.
- M5 Prompts: **complete**. Full `promptRunners` config parsing; `IPromptRunner` seam,
  `ClaudePromptRunner` (the ONLY place Claude flag spelling + `stream-json` parsing live --
  prompt via stdin, cwd = workspace, `--add-dir <planDir>`, teed to `claude-stream.jsonl`);
  `PromptComposer` (body + Shared state + Output contract + Previous-attempt feedback +
  Verdict contract); `GuardrailVerdictReader` (verdict file is the ONLY pass/fail authority).
  **needsHuman short-circuit** (SSOT section 9): escalates IMMEDIATELY -- no retry burn,
  no guardrails, no fragment merge. Prompts now run; tested tokenlessly + opt-in real-claude.
- **Reality Gate 1 + 2: MET.** `guardrails run examples/hello-guardrails/hello-guardrails`
  completes end-to-end green with the real Claude CLI (~$1.00 total).
- M6 Skills: **complete**. `plan-breakdown` SKILL.md + references (guardrail-catalogue with
  archetype table / decision tree / demotion gate / anti-patterns), `guardrails-review`
  (adversarial "cheapest wrong implementation" pass, BLOCKER/WEAK/NIT, per-finding approval),
  agents `guardrails-harness-developer`/`-skill-author`/`-test-author`/`-devils-advocate`,
  `guardrails-dev-knowledge`, lightweight `uber-report` (Reality Gate-first), and the real
  README. **Round-trip proven**: a fresh breakdown of `hello-guardrails.md` validates clean
  and matches the golden folder.
- **Reality Gate: MET -- all three booleans** (build+tests; end-to-end run on real Claude,
  verified 2026-06-10 at ~$0.90; plan-breakdown round-trip).
- M7 Polish + packaging: **complete except dogfood execution** (which awaits human review).
  Run-level cost aggregation (`JournalCost.Total`). `guardrails run --dry-run`: validates,
  prints waves preview + per-task resolution (kind/runner/retry-budget) + journal-aware
  resume SKIPs, exits 0 having touched no state. Packaging: PackageId
  `ServantSoftware.Guardrails`, version `1.0.0-preview.1`, MIT LICENSE, README packed;
  release pipeline `.github/workflows/release.yml` (tag `v*` -> 3-OS matrix -> pack ->
  `dotnet nuget push` via Trusted Publishing/OIDC). Clean-machine pack/install acceptance
  passed. **Dogfood artifact authored, not executed**: `docs/plans/04-dogfood-cost-cap.md`
  + breakdown folder await human review before `guardrails run`.
- **Plan-08 parallel execution** (branch `dogfood/plan-08`, draft PR #107, tasks 01-26
  complete): worktree isolation + reuse/chaining topology (plan branch off user HEAD,
  segment-worktree reuse for linear chains, fan-out inherit-one + fork-the-rest off W-2,
  fan-in fork); `writeScope` CHECK (deterministic read-only, triad replacement, GR2019/2020);
  per-attempt logs elevated to `logs/<runId>/<task-id>/attempt-N/` (sibling of `state/`);
  FF-free integration for linear chains (no re-verify), re-verified union for fan-in/non-FF;
  B1 atomic settle (fragment -> commit -> journal); AI-merge worker (byte producer,
  `GUARDRAILS_MERGE_BASE/_OURS/_THEIRS/_OUT` on-disk env contract, two deterministic gates,
  1-retry budget, verdict = deterministic `IReVerifier` re-verify); `scope: "integration"`
  guardrail set + terminal `integrationGate` sink (GR2017/2018); resume-by-trailer (FF commits
  carry trailers; stale segment refs deleted before any trailer read, W-1);
  `mergeOnSuccess`/`--merge-on-success` (AI-merge withheld at user-branch boundary);
  `maxParallelism: 3` default, `worktreeRoot`/`runOnCurrentBranch` config fields (GR2015/2016);
  triad (`captureHashes`/`restoreOnRetry`/`exclusive`, `WorkspaceLock`) REMOVED.
