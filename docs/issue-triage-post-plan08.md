# Open-Issue Triage — after the plan-08 (parallel-execution) merge

_Generated 2026-06-21, after PR #123 merged to `master`. All 47 open issues triaged against the merged code/skills._

## Verdict summary

| Verdict | Count | Issues |
|---|---|---|
| **Close — no longer applies** (superseded/moot) | 6 | #82, #88, #90, #91, #92, #93 |
| **Close — already applied** (fix landed in plan-08) | 6 | #54, #57, #83, #89, #95, #113 |
| **Keep — needs applying** (valid pending work) | 35 | the rest |

**→ Recommend closing 12: #54, #57, #82, #83, #88, #89, #90, #91, #92, #93, #95, #113**

**Note:** #104 and #122 are duplicates (same `.claude/` write-block) — consolidate into one, close the other.
The 35 keepers split roughly into **plan-breakdown / guardrail-catalogue / guardrails-review skill-methodology** items (the majority — orthogonal to the worktree feature) and **harness-code enhancements** (rate-limit/output-cap/timeout handling, validate lints, log UX, `.claude/` write-block).

---

## Full table

| # | Title | Verdict | Reasoning |
|---|---|---|---|
| 41 | Two-level UI verification (liveness + behavioral E2E) | Keep (skill) | Only the #64/#66 "served-markup" work exists; no Level-A/Level-B split or `$e2eStack` probe. v2-adjacent. |
| 54 | Worktree-per-task parallelism | **Close — applied** | The v2 bet plan-08 delivered: `GitWorktreeProvider`/`WorktreeHandle`/`SchedulerFactory`. |
| 57 | AI sub-agent merge-conflict resolution | **Close — applied** | Delivered: `AiMergeResolver`/`AiMergeWorker`/`GuardrailReVerifier` (re-verify after resolve). |
| 73 | Terminal e2e guardrail accepts hollow (==0/NotNull) assertion | Keep (catalogue) | No positivity-pattern / hollow-assertion doctrine in `guardrail-catalogue.md`. |
| 74 | `no-direct-bypass` archetype for injected-interface extractions | Keep (catalogue) | Archetype absent. |
| 75 | `covers-key-behaviors` guardrail for enumerated-behavior test tasks | Keep (skill) | No routing rule in SKILL.md/catalogue. |
| 76 | Extend keyword-not-structural to method-call anchoring | Keep (catalogue) | No method-call-anchoring subsection. |
| 78 | E2E interaction verification for multi-step UI flows | Keep (v2) | Roadmap bet #5; plan-08 did bets #1/#4 only. |
| 79 | Warn when a plan folder hasn't been guardrails-reviewed | Keep (harness) | No review-marker / `--skip-review-check` in `src/`. |
| 82 | Warn when workspace lacks captureHashes prefixes | **Close — moot** | captureHashes retired; new model uses git-diff in a worktree. |
| 83 | tests-build dropped → tests-fail-on-current-code must switch to build-fails | **Close — applied** | Paired in `guardrail-catalogue.md:35-42`; example uses bare `dotnet test`. |
| 84 | Insert a production-seam task when a test needs an injection seam | Keep (skill) | No "insert a seam task" guidance in the skill/stack file. |
| 85 | acceptEdits doesn't cover `.claude/` writes in worktrees | Keep (harness/runtime) | `ClaudePromptRunner.cs:131` unchanged; it's a Claude Code runtime gate. (Related: #104/#122.) |
| 86 | Agent should needsHuman on a repeated permission wall, not burn retries | Keep (harness/skill) | `PromptComposer` has only the generic escape; no permission-wall rule. |
| 87 | Limit task scope to one skill directory per task | Keep (skill) | No such sizing rule (the dogfood split 27/28/29 manually). |
| 88 | Enforcer reverts whole workspace → corrupts concurrent siblings | **Close — moot** | `WorkspaceScopeEnforcer` deleted; worktrees isolate physically. |
| 89 | IsInScope ignores literal prefix; contradicts Overlaps; matcher duplicated | **Close — applied** | Re-implemented in `WriteScope.cs` (shared `MatchSegment`); truth-table `WriteScopeMatcherTests`. |
| 90 | Enforcer snapshot/revert base mismatches workingDirectory | **Close — moot** | No whole-workspace snapshot; check diffs `taskBase..HEAD` in the task's own worktree. |
| 91 | feat/disjoint-scope-ownership is stale — reconcile with master | **Close — moot** | Branch deleted; plan-08 cut fresh from master. |
| 92 | plan-05 dogfood ran under old harness; #51 test deleted | **Close — moot** | plan-08 IS the re-run dogfood; `StateFlowTests` + new through-the-seam tests exist. |
| 93 | plan-05 review WEAK/NIT roll-up | **Close — moot** | Targets deleted enforcer/lock/plan-05. |
| 94 | Budget maxTurns by archetype; surface max_turns failures | Keep (skill/harness) | Flat default 50; no per-archetype bump or distinct surfacing. |
| 95 | Cancelled/interrupted attempts consume retry budget on resume | **Close — applied** | `TaskExecutor.cs:104` returns on Cancelled without consuming; `ResumeAndResetRetryTests`. |
| 96 | Producer↔consumer name-convention seam needs an integration guardrail | Keep (catalogue) | The third sibling seam isn't in `stacks/dotnet.md`. |
| 97 | SQL keyword guardrails must strip comments before scanning | Keep (catalogue) | No comment-stripping doctrine. |
| 98 | Forbidden-keyword guardrails must strip comments first | Keep (catalogue/skill) | Sibling of #97; same gap. |
| 99 | Corpus/aggregation completeness & substance archetype | Keep (catalogue) | Archetype absent. |
| 100 | Model large fan-out as scripted ETL, not agent-per-item | Keep (skill) | No scripted-ETL sizing doctrine. |
| 101 | Detect new-`.claude/`-subdir deliverables and seed the dir | Keep (skill) | No directory-seed doctrine. |
| 102 | Re-validate-only mode after needsHuman halt | Keep (harness) | No validate-only resume path in the CLI. |
| 103 | Attempts dropdown + durable static log pages | Keep (harness) | `LogServer` tails only latest; no selector/static export. |
| 104 | Tasks outputting to `.claude/` always needsHuman (sub-agent write block) | Keep (harness/skill) | **Duplicate of #122.** No staging/allowlist/preflight in `src/`. |
| 106 | Codify design-of-record → draft-PR review workflow | Keep (skill/agent) | Not codified in architect/dev-knowledge. |
| 108 | Large task count fails to render diagram.html | Keep (harness) | `HtmlDiagramRenderer` has no `maxTextSize`/chunking for Mermaid. |
| 109 | Worktree/dir deletion must be Windows-safe (read-only git objects) | Keep (harness/skill) | No `SafeDeleteDirectory` helper; delete sites still raw. |
| 110 | Self-review: verify every numbered deliverable maps to a task | Keep (skill) | Step 7.0 self-review still UI-only; not generalized. |
| 111 | Detect/split over-large tasks | Keep (skill) | Sizing is advisory; no split-trigger / review probe. |
| 112 | Structural property guardrails accessor-order-sensitive (`{ init; get; }`) | Keep (skill) | `dotnet.md` has no accessor-order rule; no review probe. |
| 113 | Worktree-mode gating GR2015/2017/2018 conditional on maxParallelism>1 | **Close — applied** | All three early-return for serial in `PlanValidator.cs`. |
| 114 | >32k output-token cap → opaque action-fail | Keep (harness) | No cap detection / `maxOutputTokens` config / actionable feedback. |
| 115 | Transient rate/session-limit burns retries → needs-human | Keep (harness) | No transient detection or backoff/PAUSED state. |
| 116 | Emit a Windows-safe shared `TempGitRepo` test fixture | Keep (skill) | No shared fixture / portability directive. Superset of #109. |
| 118 | Show transcript.md as the first log file | Keep (harness) | `LogServer.cs:29` still prefers `claude-stream.jsonl`. One-liner. |
| 119 | Heavy tasks exceed default timeout; timeout-retries waste budget | Keep (harness/skill) | No timeout-retry extension / continue-from-partial feedback; no per-task split heuristic. |
| 120 | plan-breakdown can omit the integration/wiring deliverable | Keep (skill) | Concrete defects (WS_1/WS_2 + wiring) landed; the **skill** ask (emit a wiring task + composition-root guardrail) is unapplied. |
| 121 | validate doesn't catch a guardrail referencing a non-ancestor's output | Keep (harness) | `PlanValidator` only checks `dependsOn` targets exist; no state/artifact-ref lint. |
| 122 | Harness can't run tasks that modify `.claude/` (writes blocked) | Keep (harness/skill) | **Duplicate of #104.** No staging/allowlist/preflight. |
