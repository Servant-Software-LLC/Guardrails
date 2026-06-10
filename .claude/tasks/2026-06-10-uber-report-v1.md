# Guardrails — Status & Readiness

**Date:** 2026-06-10  
**HEAD:** 68be0c2 (Save session context: Guardrails/Build)  
**WIP:** Working tree clean; single `master` branch; all 10 commits pushed to origin.

---

## Reality Gate

| Gate | Evidence | Pass? |
|------|----------|-------|
| Build + tests pass | `dotnet build Guardrails.sln -c Release` → 0 warnings, 0 errors. `dotnet test` → 131 Core + 36 Integration passing, 1 skipped (RealClaude smoke, intentional). | ✅ PASS |
| Walking skeleton runs end-to-end | `guardrails run examples/hello-guardrails/hello-guardrails --fresh --no-ui` — NOT RUN this session (~$1 of tokens; would fire real Claude). | ⚠️ UNVERIFIED |
| plan-breakdown round-trips | `guardrails validate examples/hello-guardrails/hello-guardrails` → "OK: plan is valid." The pre-built golden folder validates clean. Full generate-from-.md round-trip demonstrated in M6 (prior session) but not re-run this session. | ⚠️ PARTIAL |

**Verdict: WALKING SKELETON NOT PROVEN** — Gate 2 is UNVERIFIED; Gate 3 is unconfirmed this session. No milestone count substitutes for these booleans.

---

## Executive Summary

All seven milestones are code-complete and merged to master, with 167 tests passing and packaging in place (`ServantSoftware.Guardrails` dotnet tool, NuGet release pipeline). The harness architecture — Channel-based DAG scheduler, snapshot-in/fragment-out state, retry-with-feedback, ClaudePromptRunner — is structurally sound and well-tested. Three things stand between here and a confident v1: (1) the Reality Gate item 2 (end-to-end on real Claude) has never been run; (2) the dogfood plan's `tests-untouched` guardrail contains a hidden-state anti-pattern and a `Bash(git *)` gamability exploit that must be fixed before the harness runs against itself; (3) the NUGET_API_KEY secret is not yet configured, so the NuGet release pipeline is untested. The most important next action is fixing the dogfood guardrail, not releasing — the dogfood run IS the M7 exit criterion.

---

## Milestones M1–M7

| # | Exit Criterion | Verdict |
|---|---------------|---------|
| M1 | Plan committed; schemas concrete enough example folder is fully spec'd | ✅ COMPLETE — `docs/plans/` committed, `hello-guardrails/` hand-built and validate-clean |
| M2 | 2-task script plan runs end-to-end; 3-OS CI matrix in place | ✅ COMPLETE — CI matrix live on GitHub Actions |
| M3 | Kill mid-run, resume, completed tasks skipped | ✅ COMPLETE — RunJournal + resume proven by integration tests |
| M4 | 4-task diamond: break guardrail → exit 2 + blocked dependents; fix → resume green | ✅ COMPLETE — diamond fixture in `ParallelRunTests` |
| M5 | `hello-guardrails` runs fully green including prompt tasks | ✅ COMPLETE (UNVERIFIED on real Claude — fake-CLI tests pass; RealClaude smoke is opt-in/skipped) |
| M6 | `/plan-breakdown` on `hello-guardrails.md` regenerates validate-clean, structurally equivalent folder | ✅ COMPLETE per prior session (skill built and tested) — not re-verified this session |
| M7 | Clean-machine install + example green; a phase plan broken down, reviewed, executed by harness | ⚠️ PARTIAL — dogfood plan validate-clean and reviewed; execution not yet performed (awaiting guardrail fix first) |

---

## Skill Maturity

| Skill | Status |
|-------|--------|
| `plan-breakdown` | **Validated against example** — golden folder round-trips validate-clean; catalogue + worked example authored; not yet battle-tested on a real user plan beyond the dogfood |
| `guardrail-review` | **Drafted** — skill authored; applied to dogfood plan during M7; devils-advocate found that it missed the `tests-untouched` hidden-state anti-pattern in the output it reviewed |
| `guardrails-domain-knowledge` | **Battle-tested** — SELF-UPDATING, used throughout all milestones, M1–M7 status current |
| `guardrails-dev-knowledge` | **Validated** — authored in M6, reflects current layout and conventions |
| `uber-report` | **In use** — this report is the first real execution |
| Agents (5) | **Drafted** — authored in M6; used this session for read-only assessment; not yet used for actual implementation work |

---

## Findings (merged, ranked)

### HIGH

1. **Dogfood guardrail `03-tests-untouched` is a hidden-state anti-pattern AND trivially gameable** (`docs/plans/04-dogfood-cost-cap/tasks/02-implement-cost-cap/guardrails/03-tests-untouched.ps1:3`) — uses `git diff --name-only HEAD -- tests/...` which passes trivially on a fresh clone (no diff from HEAD), passes if the agent commits its changes mid-execution (allowed by `Bash(git *)`), and produces false-positives on resume. The `guardrail-review` pass did not catch this. This is a BLOCKER for executing the dogfood plan.

2. **Timeout classification in `TaskExecutor` is a string-match** (`src/Guardrails.Core/Execution/TaskExecutor.cs:814`) — detects runner timeout by matching the literal string `"timed out"` in `PromptResult.Summary`. If `ClaudePromptRunner.BuildSummary` wording changes, or a non-Claude runner uses different language, timeouts silently mislabel as generic failures, breaking feedback composition and the `TimedOut` flag.

3. **No integration test covers multi-task prompt state passing** (`tests/Guardrails.Integration.Tests/FakeClaudeRunTests.cs:33-145`) — `FakeClaudePlanBuilder` only builds single-task prompt fixtures. The critical contract — task B receives task A's merged fragment in `GUARDRAILS_STATE_IN` — has no integration-level analogue for the prompt pipeline. A bug in how `StateManager.CreateSnapshot` wires to prompt-action attempt dirs would pass the full suite.

### MEDIUM

4. **M7 exit criterion partially met** (`docs/plans/03-roadmap.md:17`) — the roadmap states the exit criterion as "a phase plan broken down, reviewed, and *executed* by the harness itself." The dogfood folder is validate-clean and reviewed, but unexecuted. The roadmap does not flag this partial state; a reader infers it only from the domain-knowledge skill. Fix: execute the dogfood plan (after fixing finding #1), then update the roadmap.

5. **SSOT §8 log-layout table is incomplete for prompt guardrail runs** (`docs/plans/02-schemas-and-contracts.md:294-298`) — the code emits `guardrail-<name>.stream.jsonl` and `composed-prompt.<name>.md` per prompt guardrail, but the SSOT §8 table only documents the action's `claude-stream.jsonl` and `composed-prompt.md`. The per-guardrail artifacts are undocumented, making the log directory shape non-deterministic for anyone writing a log parser or CI artifact collector.

6. **`TaskExecutor` is 845 lines** (`src/Guardrails.Core/Execution/TaskExecutor.cs:1-845`) — contains the attempt loop, action dispatch, prompt execution, script guardrail execution, prompt guardrail execution, environment assembly, path helpers, and three private record types. The size makes it the hardest class in the codebase to navigate or test in isolation.

7. **`PromptComposer` ordering contract untested by position** (`tests/Guardrails.Core.Tests/PromptComposerTests.cs:62-72`) — `Action_WithFeedback_IncludesPreviousAttemptSectionVerbatim` asserts both strings are present but not in order. A composer that placed feedback before the body would pass. The ordering contract (body → state → feedback → output-contract) is not pinned by any assertion.

### LOW

8. **`PromptRunnerRegistry` throw paths have no test coverage** (`src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs:62,72`) — `Resolve`/`ResolveConfig` throw `InvalidOperationException` on unknown names; the only unit test exercises the happy-path default only. No test covers the multi-runner-no-default edge case.

9. **`DryRun.ResolveRunner` duplicates `PromptRunnerRegistry` resolution logic** (`src/Guardrails.Cli/Commands/DryRun.cs:115-130`) — inline re-implementation means a future change to the registry's tiebreaker rule won't be reflected in `--dry-run` output.

10. **SSOT §2 interpreter example missing `{script}` token** (`docs/plans/02-schemas-and-contracts.md:53-54`) — the example value for `.ps1` override omits the substitution token; a reader copying it verbatim would produce a template that spawns `pwsh` with no script argument.

---

## Risk Register

| Risk | Severity | Evidence | Notes |
|------|----------|----------|-------|
| Claude CLI contract instability | LOW | `ClaudePromptRunner.cs:85-109` — all flag spelling in one class; `ClaudeStreamParser.cs:29-31` tolerant parse | Single-file quarantine; `--allowedTools` comma-join spelling undocumented in SSOT but covered by `ClaudePromptRunnerArgsTests` |
| Parallel tasks sharing one workspace | LOW | `WorkspaceLock.cs`, `Scheduler.cs:124` — prompt actions exclusive by default | Residual: no validator warning for `exclusive: false` on a prompt action |
| Plausible-but-weak generated guardrails | MEDIUM | `04-dogfood-cost-cap/tasks/02-implement-cost-cap/guardrails/03-tests-untouched.ps1:3` — hidden-state anti-pattern in the reviewed dogfood plan | The guardrail-review skill missed this; indicates the skill needs battle-testing or a stronger hidden-state check |
| Retry divergence | LOW | `TaskExecutor.cs:57-75`, `RetryPolicy.cs:77-86` | "Fix don't restart" feedback works; risk is that a 4000-char tail of confused output seeds attempt 2 — no structural isolation between attempts |
| Schema drift across docs/C#/skills | LOW | `docs/plans/02-schemas-and-contracts.md:1-6` SSOT; dogfood plan's own guardrail enforces SSOT mentions `maxCostUsd` | SELF-UPDATING instruction in domain-knowledge is unenforced by harness |
| **NEW:** `tests-untouched` guardrail gameable by `Bash(git *)` | HIGH | `04-dogfood-cost-cap/tasks/02-implement-cost-cap/guardrails/03-tests-untouched.ps1:3` + `guardrails.json:15` `allowedTools` | Agent can commit test edits mid-execution, making `git diff HEAD` return empty — direct blocker for dogfood run |
| **NEW:** `exclusive: false` on prompt action has no validator warning | LOW | No GR-code exists; `PlanValidator.cs` — no check | Low-friction foot-gun for plan authors; worktree v2 bet supersedes this |

---

## Proposed Next Actions

| Slug | Kind | Priority | Effort | Notes |
|------|------|----------|--------|-------|
| `p1-dogfood-fix-guardrail` | bug | P1 | S | Fix `03-tests-untouched.ps1` in the dogfood plan: replace `git diff HEAD` baseline with the commit SHA stored in state by task 01 (or diff against the test files' content hash at task-01-completion time). Must also close the `Bash(git commit)` timing exploit or restrict the tool permission for this guardrail. |
| `p1-dogfood-execute` | milestone | P1 | M | After fixing the guardrail: run `guardrails run docs/plans/04-dogfood-cost-cap --fresh`; this completes M7 and the first of the three Reality Gate booleans that are currently unmet. |
| `p1-nuget-release` | release | P1 | S | Configure `NUGET_API_KEY` secret on `Servant-Software-LLC/Guardrails` (Settings → Secrets → Actions), then `git tag v1.0.0-preview.1 && git push --tags` — the release workflow takes it from there. |
| `p2-reality-gate-verify` | verification | P2 | S | Run `guardrails run examples/hello-guardrails/hello-guardrails --fresh --no-ui` on real Claude (~$1) to flip Reality Gate item 2 to PASS. |
| `p2-ssot-guardrail-artifacts` | docs | P2 | S | Update `docs/plans/02-schemas-and-contracts.md` §8 to document per-guardrail `guardrail-<name>.stream.jsonl` and `composed-prompt.<name>.md` artifacts; fix the §2 interpreter example to include `{script}` token. |
| `p3-task-executor-split` | refactor | P3 | M | Extract action dispatch, prompt execution, and guardrail execution from `TaskExecutor.cs` into focused collaborator classes; bring it under 300 lines. |
| `p3-prompt-state-integration-test` | test | P3 | S | Add a `FakeClaudePlanBuilder` multi-task fixture where task B reads task A's state fragment from `GUARDRAILS_STATE_IN` and asserts the merged value. |
