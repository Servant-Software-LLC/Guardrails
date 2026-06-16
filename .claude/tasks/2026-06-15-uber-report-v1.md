# Guardrails — Status & Readiness

**Date:** 2026-06-15
**HEAD assessed:** `origin/master` `4ec33b4` (PR #28; released `v1.0.0-preview.9`) for the original findings — **updated through `8ff6dee`** (PR #29 `850afde`, PR #30 `3eb9e82`, PR #31 `8ff6dee`; released **`v1.0.0-preview.10`**). All findings are now resolved; see the Completion section.
**WIP one-liner:** All working trees clean; **no open PRs, no open issues**. The original findings assessed `origin/master` tip `4ec33b4` in a detached worktree; remediation (#29), the P3 batch (#30), and the dogfooded cost-cap slice (#31) all landed the same day and were released as `v1.0.0-preview.10`.

> **Final disposition (2026-06-15):** Reality Gate is **3/3 PASS**; M1–M7 all met; every finding in this report is `completed`. The harness has now **executed a real plan on itself** (the cost-cap slice), closing the last M7 exit criterion. See the *Completion* section for the post-`4ec33b4` follow-through; the *Findings* / *Milestones* / *Risk register* sections below preserve the original `4ec33b4` assessment for the record.

> Review method note: PR #28 (1075 lines of core prompt logic) was reviewed solo when it landed. This uber-report re-examined that surface with the full read-only agent team (harness-developer, test-author, architect, devil's-advocate) — the adversarial pass it should have had on merge.

## Reality Gate

| Gate | How checked | Verdict |
|------|-------------|---------|
| Build + tests pass | `dotnet build` + `dotnet test` (Release, clean) on `4ec33b4` | ✅ **PASS** — Core **258/258**, Integration **123/123** (+1 real-Claude smoke test correctly SKIPPED), 0 warnings |
| Walking skeleton runs | `guardrails run examples/hello-guardrails/hello-guardrails --fresh --no-ui` exits 0 | ✅ **PASS** (verified 2026-06-15, post-remediation master `850afde`) — **3/3 tasks green, exit 0, total $0.8759**; exercised the live prompt pipeline, dependency state-passing (merged seq 1), and the prompt-judge guardrail |
| plan-breakdown round-trips | a breakdown of `hello-guardrails.md` passes `guardrails validate` | ✅ **PASS** (verified 2026-06-15) — independent regeneration of `hello-guardrails.md` following the plan-breakdown skill → `guardrails validate` **exit 0**; `guardrails plan` → **3 waves** (1→2→3). Deterministic CI half also exists (`GoldenRoundTripTests`, PR #29). The live round-trip remains a manual/dogfood demo, not a CI-runnable test. |

**Verdict: Reality Gate 3/3 PASS.** Build+tests green; the walking skeleton runs end-to-end on real Claude; and the live `/plan-breakdown` round-trip is now demonstrated. No part of the verdict is inferred — every gate was run.

## Remediation (post-report — PR #29, squash-merged as `850afde`)

All **P1/P2** findings were landed the same day via the specialized agent team (harness-developer ×2, architect, test-author) in file-disjoint worktrees, lead-reviewed, 3-OS CI green (Core 264 / Integration 124 +1 skip, `graph --check` 0), then the walking skeleton was re-verified live (above). Status of each:

| Slug | Pri | Status |
|------|-----|--------|
| `scheduler-worker-swallows-non-cancellation` | P1 | ✅ Fixed — fault recorded, run-scoped CTS cancels siblings, `RunAsync` rethrows → always terminates; regression test added |
| `golden-roundtrip-test-missing` | P1 | ✅ Deterministic CI half added (`GoldenRoundTripTests`); live `/plan-breakdown` regen remains the manual/dogfood demo |
| `dependency-context-stale-attempt-pointer` | P2 | ✅ Fixed — `CurrentSuccessfulAttempt` uses on-disk `fragment.json` as current-state provenance; regression test added |
| `logs-command-absent-from-ssot` / `p2-ssot-guardrail-artifacts` | P2 | ✅ Fixed — SSOT §12 (logs/log-server) + §8 (per-guardrail artifacts) + §2 `{args}` |
| `schemas-md-duplicate-no-drift-test` | P2 | ✅ Fixed — `promptRunners` block made byte-identical w/ sentinels + `SchemaDriftTests` |
| `crlf-normalization-unpinned` | P2 | ✅ Resolved via `.gitattributes` (`eol=lf` on byte-sensitive assets) + observable-contract tests. *Nuance:* a unit tripwire on the internal `Replace` is unachievable — the renderer is independently `\r`-robust, so that `Replace` is redundant defense |
| `p1-dogfood-fix-guardrail` | P1 | ✅ Was already content-hash-based on disk (carry-forward was stale vs current master); hardened to fail loudly on `git hash-object` error |
| `p2-reality-gate-verify` | P2 | ✅ Done — walking skeleton 3/3 green, $0.8759 (Reality Gate #2 → PASS) |

**(Originally deferred, now closed below):** `p1-dogfood-execute`, all **P3** items, and `p3-task-executor-split` — all landed in the Completion follow-through. The findings further below reflect the **original** as-of-`4ec33b4` assessment; the tables here record current disposition.

## Completion (post-remediation — PR #30, PR #31, Reality Gate #3 — 2026-06-15)

After remediation (#29), the remaining backlog was cleared the same day and released as **`v1.0.0-preview.10`**.

**P3 batch — PR #30, squash-merged as `3eb9e82`** (agent team, file-disjoint, lead-reviewed, 3-OS CI green):

| Slug | Pri | Status |
|------|-----|--------|
| `transcript-truncate-splits-surrogates` | P3 | ✅ Fixed (21e48bb) — `Truncate` respects UTF-16 surrogate boundaries; no mid-pair `U+FFFD` |
| `transcript-determinism-overclaim` | P3 | ✅ Fixed (21e48bb) — docstring scoped to "deterministic for a given input stream" |
| `logserver-perf-rereads-file-per-poll` | P3 | ✅ Fixed (37f4fcf) — stale-proof cache, no full re-read each ~1s poll |
| `logserver-port-probe-toctou` | P3 | ✅ Fixed (37f4fcf) — race-resilient bind replaces the probe-then-rebind TOCTOU |
| `readme-exit2-and-command-table-drift` | P3 | ✅ Fixed (001afa8) — exit-2 wording matches SSOT §7; `lock`/`merge` added to the command table |
| `p3-task-executor-split` | P3 | ✅ Done (988b58a) — `TaskExecutor` split into action-dispatch / prompt / guardrail collaborators |
| `logs-status-mapping-untested-e2e` | P3 | ✅ Fixed (04f6ca0) — e2e test exercises the real `StatusText` mapping (no bypassing lambda) |

**Dogfood execution + cost cap — PR #31, squash-merged as `8ff6dee`** (closes M7's real exit criterion and `cost-recorded-not-capped`):

`guardrails run docs/plans/04-dogfood-cost-cap` drove the **first v2 slice** (the per-run cost cap, `maxCostUsd`) to green **with the harness running on itself** — 4/4 tasks: `01` authored failing tests (tests-fail-on-current-code proven), `02` implemented to green (Release build + `~CostCap` 10/10 + tests-untouched guardrails), `03` documented `maxCostUsd` in SSOT §2 + the domain skill, `04` whole-suite Release gate. Total ~$12.66. Task `03` honestly **halted `needs-human`** at a `.claude/` write boundary (the harness is denied writes to its own skills); a human applied the one skill edit and **resumed**, after which the guardrail verified it — the honest-halt → human → resume loop working as designed. Validation deviation noted in-code: GR2010→**GR2012** (GR2010/GR2011 were taken by stableId checks that landed after the slice was planned).

**Reality Gate #3 — PASS (2026-06-15):** an independent `/plan-breakdown` of `hello-guardrails.md` validated clean (`exit 0`) and planned to 3 waves — the live round-trip the deterministic `GoldenRoundTripTests` (#29) couldn't cover.

**Release `v1.0.0-preview.10`** (`8ff6dee`): release run `27589652767` — all 3 OS test jobs + `pack and publish to NuGet.org` succeeded (`ServantSoftware.Guardrails`, OIDC).

**Net result: Reality Gate 3/3, M1–M7 all met, every finding `completed`.**

## Executive summary

> *`4ec33b4` snapshot — superseded by the Remediation + Completion sections. The two safety nets called out below as "still do not exist" now both exist/ran: the golden round-trip (deterministic half in CI, #29; live round-trip verified, Reality Gate #3) and the dogfood execution (#31). The scheduler resilience hole is fixed (#29). Reality Gate is now 3/3 against real Claude.*

The harness is **built broadly and tested deeply against a fake CLI, but never demonstrated against real Claude in CI**, which is exactly the gap the Reality Gate exists to keep visible. Code-level health is good — path-traversal defenses on the new log server are solid, retry feedback is size-bounded, and the determinism work from #3 holds in the unit tests. The strongest *new* finding is a real resilience hole: the scheduler's worker loop swallows only cancellation, so any other exception from a task escapes the worker and the run can hang without completing (`Scheduler.cs:118-145`). The strongest *structural* gap is that two milestone-exit safety nets the docs claim — the **golden plan-breakdown round-trip test** (M6 / risk row 5) and the **dogfood execution** (M7) — still do not exist. NuGet release (an open item at the last report) is now **done** (preview.1→preview.9 shipping via OIDC). `TaskExecutor.cs` has grown from 845→**980 lines** and remains the repo's main refactor debt.

## Milestones M1–M7 (exit-criterion verdicts)

> *Snapshot as of `4ec33b4` (pre-remediation). **Current state: M1–M7 all met** — see the Completion section. M2/M5 live-verified by Reality Gate #2; M6 by Reality Gate #3; the M4 resilience gap fixed in #29; M7 dogfood executed in #31.*

| # | Exit criterion | Verdict | Evidence |
|---|----------------|---------|----------|
| M1 | Plan committed; example fully spec'd | ✅ Met | `docs/plans/00–03`, `examples/hello-guardrails/` present and spec-complete |
| M2 | 2-task plan runs end-to-end Win+Linux; 3-OS CI | ⚠️ **Built, not live-verified** | 3-OS CI matrix exists & green; deterministic/script tasks proven by tests, but end-to-end run is gated behind the UNVERIFIED Reality Gate |
| M3 | Kill mid-flight, resume, skip completed | ✅ Met (test-demonstrated) | Resume/skip covered by integration tests; StateManager snapshot/merge tested |
| M4 | Diamond: break guardrail → exit 2 + blocked dependents; fix → resume green | ⚠️ **Mostly met, one resilience gap** | Exit-2/blocked-dependents path tested; **but** scheduler swallows only cancellation (`scheduler-worker-swallows-non-cancellation`) → non-guardrail throws can hang the run |
| M5 | `hello-guardrails` runs fully green incl. prompt tasks | ⚠️ **Built, not live-verified** | Prompt pipeline + verdict contract + feedback injection all unit/integration tested with fake CLI; real-Claude smoke test SKIPPED in CI |
| M6 | `/plan-breakdown` regenerates a validate-clean, **structurally equivalent** folder | ❌ **Not demonstrated** | The golden round-trip test does **not** exist (`golden-roundtrip-test-missing`); skill `references/schemas.md` is a 2nd un-drift-tested copy of the SSOT |
| M7 | Clean-machine acceptance; a phase plan **executed by the harness itself** | ❌ **Not met** | Domain-knowledge headline "complete except dogfood" overstates; dogfood plan authored but never executed; its guardrail still has the git-diff exploit (`p1-dogfood-fix-guardrail`). NuGet-publish half of M7 ✅ done |

## Skill maturity

> *`4ec33b4` snapshot. **Updated:** `plan-breakdown` is now round-trip-proven (Reality Gate #3) and battle-tested-on-a-real-plan (it generated the dogfooded cost-cap slice, #31); the `references/schemas.md` drift risk is closed by `SchemaDriftTests` (#29); `guardrails-domain-knowledge`'s Status section no longer over-claims — M6/M7 are genuinely complete.*

| Skill | Maturity |
|-------|----------|
| `guardrails-domain-knowledge` | battle-tested — but Status section over-claims M6/M7 (see findings) |
| `guardrails-dev-knowledge` | validated-against-example |
| `plan-breakdown` | drafted + doctrine-rich — **not** proven by a round-trip meta-test (M6 hole) |
| `guardrails-review` | drafted |
| `uber-report` | battle-tested (this run; 2nd issuance) |
| `references/schemas.md` (in skill) | **drift-risk** — hand-maintained copy of SSOT, no drift test |

## Findings (merged, deduped, ranked)

> *Original `4ec33b4` findings, preserved for the record. **All are now `completed`** (#29/#30/#31) — see the Remediation + Completion sections and the `.findings.json` index for per-slug disposition.*

Each is falsifiable and cited. Routes: `direct` / `execute-tasks` / `guardrails` / `decide`.

1. **`scheduler-worker-swallows-non-cancellation`** (P1, bug, `guardrails`) — `Scheduler.cs:118-145`: the worker loop's `catch` handles only `OperationCanceledException`. Any other exception out of `ExecuteAsync` (e.g. an `InvalidOperationException` from a missing registry, an IO error) escapes the worker task; `Remaining` is never decremented and the channel is never completed → the run can hang instead of failing cleanly. Falsify: inject a task whose executor throws a non-OCE; observe whether `run` terminates with a non-zero exit. **Needs an authored regression test → plan-worthy.**
2. **`golden-roundtrip-test-missing`** (P1, test/milestone, `guardrails`) — M6 exit criterion and risk-register row 5 both promise a "golden round-trip test in CI," but no test regenerates `hello-guardrails` via plan-breakdown and asserts structural equivalence + `validate`-clean. This is the safety net that keeps generated folders honest; its absence means M6 is unproven. Falsify: grep `tests/**` for any plan-breakdown round-trip — none exists.
3. **`crlf-normalization-unpinned`** (P2, test, `execute-tasks`) — `ClaudeTranscriptRenderer` CRLF→LF normalization (`:49`) is not pinned by a test that feeds explicit `\r\n`; there is no `.gitattributes`, so the golden fixture is LF on Linux. Deleting the normalization would still pass CI — i.e. the exact CRLF-hash bug class fixed in #3 could silently regress. Falsify: remove the `Replace("\r\n","\n")` and run the suite green.
4. **`schemas-md-duplicate-no-drift-test`** (P2, docs/test, `execute-tasks`) — the `plan-breakdown` skill's `references/schemas.md` is a second, hand-maintained copy of the SSOT (`02-schemas-and-contracts.md`) schema section with no test asserting they agree. Drift is invisible until a generated folder fails `validate`. Falsify: edit one copy's enum and watch nothing fail. (Overlaps risk row 5.)
5. **`logs-command-absent-from-ssot`** (P2, docs/contract, `execute-tasks`) — `guardrails logs` and the `run --log-server` flags are registered and documented in README but have **no contract section in the SSOT** (`02-schemas-and-contracts.md`), violating the "SSOT changes in the same change" invariant. Supersedes/extends carry-forward `p2-ssot-guardrail-artifacts` (§8 guardrail artifacts still undocumented too). Falsify: grep SSOT for `logs` / `log-server` — absent.
6. **`dependency-context-stale-attempt-pointer`** (P2, bug-risk, `guardrails`) — `TaskExecutor.BuildDependencyContext` selects `Attempts.LastOrDefault(a => a.Outcome == Succeeded)`; after a `reset`/re-run, that transcript/fragment may not be the artifact recorded in the *current* `state.json`, so a prompt could cite a stale dependency. Falsify: succeed task A, reset+re-succeed A, inspect whether B's composed prompt points at the first run's transcript. Needs design thought → plan-worthy.
7. **`logs-status-mapping-untested-e2e`** (P3, test, `execute-tasks`) — `LogsCommand`'s journal→status-word mapping (`StatusText` switch) is never exercised end-to-end; tests inject a lambda that bypasses it. Falsify: change a status word and watch all tests stay green.
8. **`readme-exit2-and-command-table-drift`** (P3, docs, `direct`) — README says exit `2` = "needs-human," but SSOT §7 defines `2` as the general actionable-signal code (also `graph --check` stale, `lock --check` drift, merge conflicts); README's command table also omits the registered `lock` and `merge` commands. Two small doc corrections.
9. **`cost-recorded-not-capped`** (P3, risk-accuracy, `decide`) — risk row 1's "max-turns/timeout **cost** breakers" overstates: those are turn/time breakers; there is **no spend ceiling**, only `total_cost_usd` recording. Unattended runs cannot trip needs-human on cost. (Cost caps are explicitly v2 — this is a wording/expectation fix, not a v1 gap.)
10. **`transcript-truncate-splits-surrogates`** (P3, bug, `direct`) — `ClaudeTranscriptRenderer.Truncate` slices on UTF-16 code units (`value[..max]`, ~`:315-316`); a cut mid-surrogate-pair yields a `U+FFFD` in the transcript. Cosmetic, deterministic, easy fix.

**Low / fold-in (noted, not separately tracked):** transcript docstring "never varies run-to-run" holds only for identical input streams (`transcript-determinism-overclaim`, `direct`); log server re-reads the whole file each 1s poll (`logserver-perf`, `decide`); `FreeLoopbackPort` closes its probe before `HttpListener` rebinds → TOCTOU flake under parallel (`logserver-port-probe-toctou`, `decide`).

**Could-not-break (honesty — verified strong, no change needed):** Log-server **path-traversal** defense is solid — `IsSafeFileName` + `Path.GetFullPath` containment check + task-id whitelist + numeric loopback host + `nosniff`/`DENY` headers; the devil's-advocate could not escape the log directory. Retry **feedback size** is bounded (`TailLines=60`, `TailChars=4000`). These confirm the #24/#28 review held up under the team's adversarial pass.

## Risk register (seeded rows from `03-roadmap.md` + run-specific)

> *`4ec33b4` snapshot. **Updated after #29/#30/#31:** row 1 — a real spend ceiling now exists (`maxCostUsd`, #31), so "cost breakers" is no longer overstated; row 3 — the dogfood (the strongest guardrail-quality test) has now **run** green (#31); row 5 — the live golden round-trip is verified (Reality Gate #3) and the deterministic half is in CI (#29); R-run-a — scheduler non-OCE hang fixed (#29); R-run-b — live e2e demonstrated (Reality Gate #2).*

| # | Seeded risk | This run's verdict |
|---|-------------|--------------------|
| 1 | Claude CLI contract instability — quarantined in `ClaudePromptRunner`; verdict files not exit codes; max-turns/timeout cost breakers | **PRESENT & effective** for the contract-quarantine part; **"cost breakers" overstated** — no spend cap exists (see `cost-recorded-not-capped`) |
| 2 | Parallel tasks sharing one workspace — exclusive-by-default; honest docs; worktrees are v2 | **PRESENT & effective** — `WorkspaceLock` FIFO + exclusive-by-default for prompt actions |
| 3 | Plausible-but-weak generated guardrails — comments, demotion gate, tests-fail-on-stub, review, dogfooding | **PRESENT as doctrine; weakest layer unproven** — the dogfood (strongest test) has never run, and its own guardrail has a git-diff exploit |
| 4 | Retry divergence — low retries, "fix don't restart" feedback, full logs | **PRESENT & strengthened by #28** — bounded prior-attempt pointers + tail-capped feedback |
| 5 | Schema drift across docs/C#/skills — SSOT + `validate` gate + golden round-trip test in CI | **WEAKER than claimed** — the golden round-trip test does **not** exist; `references/schemas.md` is an un-drift-tested duplicate (see findings 2 & 4) |
| R-run-a | Run resilience to non-cancellation exceptions | **NEW** — scheduler can hang on a non-OCE throw (finding 1) |
| R-run-b | Live e2e never demonstrated in CI | **NEW/standing** — real-Claude smoke test skipped in CI; Reality Gate #2/#3 UNVERIFIED |

## Proposed next actions (slug-keyed)

Strongest-first; each maps to a finding/carry-forward slug and a route.

1. `scheduler-worker-swallows-non-cancellation` → **guardrails** (plan it; authored regression test). The one finding that can hang a real run.
2. `golden-roundtrip-test-missing` → **guardrails**. Closes the M6 exit criterion and risk row 5 in one stroke; also retires `schemas-md-duplicate-no-drift-test` if the round-trip asserts both copies.
3. `p2-reality-gate-verify` (carry-forward) → **decide** then run. One ~token run of `hello-guardrails` flips Reality Gate #2 (and most of M2/M5) from UNVERIFIED to PASS. **I can run this now if you authorize the token spend.**
4. `p1-dogfood-fix-guardrail` → **execute-tasks**, then `p1-dogfood-execute` → **guardrails**. Unblocks M7's real exit criterion.
5. `logs-command-absent-from-ssot` + `p2-ssot-guardrail-artifacts` (carry-forward) → **execute-tasks**. Restore the SSOT-same-change invariant for the log/logs surface and §8 artifacts.
6. `crlf-normalization-unpinned`, `dependency-context-stale-attempt-pointer`, `logs-status-mapping-untested-e2e` → **execute-tasks** / **guardrails** as marked.
7. `readme-exit2-and-command-table-drift`, `transcript-truncate-splits-surrogates` → **direct** (trivial, batch them).
8. `p3-task-executor-split` (carry-forward, now 980 lines) → **execute-tasks** when convenient — debt, not a defect.

**Carry-forward status:** `p1-nuget-release` → ✅ **completed** (preview.1→preview.9 via OIDC). Still open: `p1-dogfood-fix-guardrail`, `p1-dogfood-execute`, `p2-reality-gate-verify`, `p2-ssot-guardrail-artifacts` (folded into #5 above), `p3-task-executor-split`, `p3-prompt-state-integration-test` (overlaps `dependency-context-stale-attempt-pointer`).
