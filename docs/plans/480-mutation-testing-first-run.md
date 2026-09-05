# 480 — Scoped first Stryker.NET run: findings

**Date:** 2026-09-05 · **Branch:** `feat/480-stryker-first-run` (based on `d7799d40`)
**Tool:** Stryker.NET `dotnet-stryker` 4.16.0 · **Platform:** Windows 11, 8 logical cores, .NET SDK 10.0.204

> Location note: this is a durable measurement note, not a plan of record. It goes in
> `docs/plans/` because that is the existing convention and because the issue-numbered
> siblings (`585-…`, `595-…`) already use it. `docs/notes/` does not exist; creating it
> for one file would have been the drift, not the tidiness.

---

## Headline

**The run is blocked. Stryker.NET 4.16.0 cannot produce a valid mutation score against
this repository, and it fails in the worst possible way: silently, confidently, and with
a full report of fabricated findings.**

Against the live-surface scope it printed:

```
[22:53:44 INF] Time Elapsed 00:17:55.0421652
[22:53:44 INF] The final mutation score is 0.00 %
```

…with 59 of 59 mutants marked `Survived`, each with a file, a line, a mutator name and a
diff. **Every one of those 59 survivors is fictitious.** Stryker never activated a single
mutant; the tests ran against unmutated code, all passed, and Stryker recorded "nothing
killed this" 59 times.

This is the same defect archetype the exercise was commissioned to hunt — an assertion
that cannot fail — relocated into the measuring instrument itself.

### The proof

Stryker reported this mutant, in `src/Guardrails.Cli/Ui/LiveTableRows.cs:110`, as `Survived`:

```csharp
if (waves.Count == 0)   →   if (waves.Count != 0)
```

Applied by hand to the source and built normally, that exact mutant **fails 15 of the same
170 tests in 13 seconds** — `CollapseCompletedWavesTests`, `ModelInRowTests` and others.

```
Failed!  - Failed: 15, Passed: 155, Skipped: 0, Total: 170, Duration: 13 s
```

The oracle is not blind to this mutant. Stryker is blind to the oracle.

Reverted immediately after measuring; the tree carries no source change.

---

## Scope actually run

The issue's proposed scope ("start with `Guardrails.Core`, exclude the integration
project") was corrected on the issue and is not what was run — it misses the only measured
survivor on both axes. What was run:

| | |
|---|---|
| Mutated project | `src/Guardrails.Cli/Guardrails.Cli.csproj` |
| Mutated files | `Ui/LiveRunObserver.cs`, `Ui/LiveNarrative.cs`, `Ui/LiveTableRows.cs` (1,459 lines) |
| Oracle | `tests/Guardrails.Integration.Tests` — the 15 classes referencing those types (**170 tests**) |
| Mutants generated | 7,914 (whole Cli project) |
| Mutants in scope | 369 (all three files) / 59 (the two pure files, the run that completed) |
| Compile-error mutants **in scope** | 0 |

`Guardrails.Core.Tests` is not an available oracle for this project at all — it does not
reference `Guardrails.Cli`, so it cannot execute a line of it. That is a structural fact,
not a choice.

### Explicit test exclusions, as required

The brief asked that `Core.Tests`'s process-spawning and git-walking classes be excluded by
**explicit test filter**, and that the exclusion be stated. It is stated here:

- For the run that was actually executed, **no exclusion was needed**: the oracle is
  `Integration.Tests` only, and `WorktreeJunctionTests`, `WorktreeContainmentHookTests`,
  `ProcessRunnerHermeticEnvTests` and `ProducerCoverageCorpusTests` all live in
  `Core.Tests`, which cannot reach `Guardrails.Cli`. They were never in the oracle.
- For the Core half (`stryker-config.core-execution.json`, never run), the exclusion is
  written as a real `test-case-filter` and its cost is named in the config: any
  Scheduler/TaskExecutor mutant whose only killer was one of those four classes will be
  reported as a survivor.

Stryker's `test-case-filter` was verified to work under the VSTest runner: it cut
discovery from 1,258 tests to 170. It is **silently ignored** under the MTP runner, which
still discovered 1,258.

---

## Root cause

Two independent symptoms, one cause: **Stryker's test-host instrumentation never attaches
to an xunit.v3 test process.**

1. **VSTest runner** — coverage capture fails on every attempt:
   ```
   [ERR] It looks like the test coverage capture failed. Disable coverage based optimisation.
   ```
   Reproduced 3/3 times, including with `VSTEST_CONNECTION_TIMEOUT=900` and concurrency
   lowered to 2, so it is not the machine-slowness case Stryker's own hint describes. The
   same broken channel that carries coverage also carries *which mutant is active*, so
   mutant activation fails with it — but that half fails **without any error at all**.

2. **MTP runner** (`-t mtp`, Stryker calls it preview) — hard failure after 6m28s:
   ```
   No test result reported. Make sure your test project contains test and is compatible with VsTest.
   ```

The likely mechanism: an xunit.v3 test project builds as its own executable with an
in-process runner (`Guardrails.Integration.Tests.exe` reports itself as
*"xUnit.net v3 In-Process Runner v3.2.2"*). `xunit.runner.visualstudio` proxies to that
process, so a VSTest **data collector** — which is what Stryker injects — attaches to the
test host, not to the process actually executing the tests.

Both test projects in this repo are xunit.v3 3.2.2, so this blocks the Core half too.

---

## Cost, measured

Two caveats before any number below is quoted.

1. **The box was contended.** Two other worktrees (`478-pre-satisfied-clause`,
   `637-attach-clock`) had concurrent sessions running .NET suites throughout. Every
   wall-clock figure here is inflated by an unknown factor, and CI's own step timings are
   the better source.
2. `--coverage-analysis perTest`, which the brief nominated as the cost lever, **does not
   apply here** — the capture fails and Stryker falls back to running the whole oracle
   against every mutant. The cost model in the issue's triage assumed it works.

| measurement | value |
|---|---|
| Full suite baseline, this box | Core 2,577 tests / 7m08s · Integration 1,258 tests / 13m58s |
| Stryker initial run, **full 1,258-test oracle** | **50 minutes** |
| Stryker initial run, 170-test oracle | ~2 minutes |
| Plain `dotnet test`, same 170 tests | **15 seconds** |
| 59 mutants, 2 pure files, concurrency 2 | 17m55s |
| 369 mutants, all three files | abandoned at 53 min, no result, projected ~3 hours |

Three things worth keeping from that table:

- **Stryker must run the oracle twice before testing any mutant** (initial run, then
  coverage capture). With the full Integration suite that is ~100 minutes of setup before
  a single mutation result exists, because dozens of those tests spawn real harness runs
  that themselves invoke `dotnet build` and `dotnet test`.
- **Stryker's harness costs ~8× the tests it runs**: 15s of tests became ~2 minutes.
- **A surviving mutant is the expensive case** — with bail on, a killed mutant stops at the
  first failing test; a survivor runs all 170. A tool that marks everything survived is
  therefore also the slowest possible configuration, which is why the 369-mutant run
  projected to three hours.

---

## Calibration — what this run does and does not certify

The brief asked for this explicitly, and it matters more than usual given the result.

**Does not certify:**

- **Nothing about test quality.** No mutant was ever executed. The 0.00% is not a finding
  about the suite; it is an artifact of a mute tool. It would be a serious error to open
  coverage-gap work off that report.
- **A clean sheet over `LiveRunObserver.cs` would not have proved the config works even if
  the tool did.** Stryker's standard mutators — operators, literals, statement/block
  removal, LINQ and string substitution — **cannot express the #635 defect**, which was a
  method-call substitution (`AppendNarrative(...)` → `_console.MarkupLine(...)`). The
  mutator list from the run that completed confirms it: Equality, Statement, String,
  Negate, Conditional, Block removal, Unary, Increment. No call-target substitution among
  them. The known survivor is out of this tool's reach by construction.
- **One real guard is invisible to Stryker regardless.**
  `LiveNarrativeCompositeTests.LiveRunObserver_ContainsNoOutOfBandConsoleWrite_OnlyTheInjectionDefault`
  reads `LiveRunObserver.cs` **off disk** via `[CallerFilePath]`. Stryker mutates the
  compiled assembly and leaves the `.cs` untouched, so that test passes under every mutant
  and can never kill one. It is a good guard; mutation testing simply cannot score it. Any
  future score over `LiveRunObserver.cs` is a score over the *behavioural* tests only.

**Does certify, modestly:**

- The **config's scoping is correct and verified**: right project resolved, right three
  files mutated, 170-test oracle selected by filter, zero compile-error mutants inside the
  mutated scope.
- **One real datapoint about the suite**, obtained by hand rather than by Stryker: the
  `waves.Count == 0` → `!=` mutant in `LiveTableRows.Plan` is killed by 15 tests. That
  assertion cluster is not blind. It is one mutant out of 369 in scope and 7,914 in the
  project; it settles nothing general.

**The survivor list and the "3-5 most interesting survivors" cannot be delivered.** The
only survivor list produced is fabricated, and triaging fabricated survivors into "genuine
gap / equivalent mutant / timeout" would manufacture exactly the false confidence this
issue exists to remove. That deliverable is deferred to a run whose instrument works.

---

## Secondary findings

1. **Stryker runs in place and does not clean up after an abort.** It swaps a mutated
   `Guardrails.Cli.dll` (775,680 bytes) into `tests/Guardrails.Integration.Tests/bin/` and
   keeps the original (577,024 bytes) beside it as `Guardrails.Cli.dll.stryker-unchanged`.
   Killing the run leaves the **mutated** DLL in place, so a subsequent
   `dotnet test --no-build` silently tests mutated code. Hit and manually recovered three
   times during this session. Always pass `-O <path outside the repo>`, and check for
   `*.stryker-unchanged` after any interrupted run.

2. **`TreatWarningsAsErrors` was a non-issue — the stated hazard did not materialise.**
   All 16 compile-error rollbacks were `CS0165` (use of unassigned local), a genuine
   compiler error. Zero were analyzer diagnostics: Stryker compiles with its own Roslyn
   compilation, which does not run the CA/xUnit analyzers or honour the MSBuild
   warnings-as-errors setting. The 1,484 compile-error mutants (18.8% of 7,914) come from
   definite-assignment, not from repo build policy.

3. **Safe-mode rollback is coarser than it sounds.** One `CS0165` causes
   *"Stryker will remove all mutations in `<method>`"* — 16 bad mutants blanked **every**
   mutant in 14 methods, `GuardrailHeartbeat.Tick`, `LogServer.AcceptLoopAsync` and
   `LogSiteRenderer.AppendPhaseEvidence` among them. Those methods would be silently
   unmeasured in any future run, and nothing in the score says so.

4. **A pre-existing test is machine-load-dependent.**
   `Guardrails.Core.Tests.Webhooks.WebhookEventSinkTests.AFaultedPumpIsReportedNotSummarizedAsZero`
   failed in the contended baseline at 3.25s against
   `Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), ...)`, and passes in
   isolation. This is the "assert the decision, not the duration" anti-pattern: a
   wall-clock bound cannot distinguish a wrong behaviour from a busy runner. It should
   assert what teardown *decided* (that it stopped early and named the unreached rows —
   which the same test already does further down) rather than how long it took. Worth its
   own issue; not fixed here, since this branch is deliberately test-infrastructure only.

---

## Recommendation

**Do not schedule a permanent Stryker job, and do not put one on the PR path. Close out
this attempt as blocked-on-tooling.**

The cost evidence is real but secondary — even at CI's ubuntu timings (whole solution in
86s) the two mandatory full-suite passes plus a survivor-heavy mutation phase make this a
tens-of-minutes-to-hours job, which is on-demand work at best. The decisive point is
simpler: **there is nothing to schedule.** A nightly job that reports a confident 0.00%
over fabricated survivors is worse than no job — it is a green-adjacent number attached to
a mechanism that measured nothing, in a product whose thesis is *"a prompt may propose,
only a deterministic gate may certify."*

Order of operations for whoever picks this up:

1. **Confirm the incompatibility upstream.** The failing combination is Stryker.NET 4.16.0
   + xunit.v3 3.2.2 + `xunit.runner.visualstudio` 3.1.5 + .NET 10. A minimal repro is
   cheap. If it is a known issue, that determines whether to wait or work around.
2. **Add a self-check before any scheduled run is ever considered.** This exercise argues
   its own conclusion: any mutation-testing job must first prove it can kill a mutant it is
   *known* to be able to kill — a planted canary mutant that must come back `Killed`, or
   the job fails. The `waves.Count == 0` → `!=` mutant is a ready-made canary. Without
   that gate, the tool reproduces the exact defect class it was bought to find.
3. **Only then re-scope.** The scoping work in the two committed configs is done and
   verified; it is the instrument that needs replacing, not the aim.

Also worth naming so the two are not conflated: `docs/plans/22-run-authored-test-trust.md`
§7 lists mutation testing behind an evidence gate for the tests a *run authors*. Different
target, same tool, same blocker.

---

## Verification gate

Clean `dotnet test Guardrails.sln` (builds first, so nothing runs against a leftover
mutated assembly), after all mutation work and with the tree restored:

| suite | passed | failed | skipped | total | duration |
|---|---|---|---|---|---|
| `Guardrails.Core.Tests` | 2,577 | **0** | 0 | 2,577 | 5m21s |
| `Guardrails.Integration.Tests` | 1,254 | **0** | **4** | 1,258 | 10m41s |
| **total** | **3,831** | **0** | **4** | **3,835** | — |

Zero build warnings (`TreatWarningsAsErrors` is on repo-wide, so that is enforced).

The 4 skips are named rather than summarised, because a skip is evidence that did not get
collected:

- `RealClaudeSmokeTests.TrivialPromptTask_RunsGreen_AgainstRealClaude`
- `RealClaudeClaudeNestedPlanTests.PromptActionTask_ClaudeNestedPlan_WorktreeMode_RunsGreen_AgainstRealClaude`
- `ModelTiering.NoRoutingGoldenTests.FreshBreakdown_ReproducesTheGoldenByteForByte`
- `ModelTiering.NoRoutingNegativeAssertionTests.FreshBreakdown_CarriesNoTierArtefacts`

These are the opt-in real-Claude tests (`GUARDRAILS_REAL_CLAUDE=1`) and their model-tiering
siblings — skipping them here is the intended house behaviour, not a regression.

Note also that `WebhookEventSinkTests.AFaultedPumpIsReportedNotSummarizedAsZero`, which
failed in the contended baseline, **passed** in this quiet run — confirming secondary
finding 4 is load-sensitivity rather than a defect in the code under test.

**Tree state:** no source file was modified by this branch. The one hand-applied mutant was
reverted immediately after measurement, and `git status` shows only the three added files.
No `*.stryker-unchanged`, `StrykerOutput/` or `.stryker-tmp/` remains anywhere in the
worktree; all Stryker output was directed outside the repo with `-O`.

---

## Artifacts

- `stryker-config.json` — live-surface scope. Carries a loud do-not-trust header.
- `stryker-config.core-execution.json` — Core `Scheduler.cs` / `TaskExecutor.cs` scope with
  the explicit `test-case-filter` exclusions. Never run; same blocker.
- Neither is wired into `ci.yml`, by design.
