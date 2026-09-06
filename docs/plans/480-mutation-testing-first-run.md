# 480 — Scoped first Stryker.NET run: findings

**Date:** 2026-09-05 · **Branch:** `feat/480-stryker-first-run` (based on `d7799d40`)
**Tool:** Stryker.NET `dotnet-stryker` 4.16.0 · **Platform:** Windows 11, 8 logical cores, .NET SDK 10.0.204

> Location note: this is a durable measurement note, not a plan of record. It goes in
> `docs/plans/` because that is the existing convention and because the issue-numbered
> siblings (`585-…`, `595-…`) already use it. `docs/notes/` does not exist; creating it
> for one file would have been the drift, not the tidiness.

> **PARTLY SUPERSEDED — 2026-09-06.** Everything below about **released 4.16.0** still
> holds and was re-reproduced. But the blocker is **fixed upstream, on master, unreleased**.
> A source build of `stryker-net@55464f4` run with `--test-runner mtp` **killed a mutant
> that 4.16.0 called Survived.** Read
> [Follow-up: source build of master](#follow-up-source-build-of-master-2026-09-06) before
> acting on the recommendation in this document — the verdict changed from
> *blocked-on-tooling* to *works, but the cost model does not*.

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

---

## Follow-up: source build of master (2026-09-06)

**Date:** 2026-09-06 · **Tool:** `dotnet-stryker` built from source at
`stryker-net@55464f49092e379faf6762003117259d570cd409` (master, 2026-09-05 13:07 UTC),
packed as `4.17.0-master-55464f4`, installed to a **local tool path**
(`--tool-path`, never `-g`) so the machine's global tools are untouched.
Same box: Windows 11, 8 logical cores, .NET SDK 10.0.204.

The released 4.16.0 (2026-07-03) predates the fix. [PR #3752][pr3752] —
*"feat(MTP): Support `perTest` and `perTestInIsolation` coverage analysis"* — merged
**2026-08-14** as `0705367`, six weeks after that release, and there is no preview or
nightly on NuGet. `git merge-base --is-ancestor` confirms `0705367` (and the follow-up
`eb94878`, *"MTP coverage per mutated assembly"*, [PR #3769][pr3769]) are both ancestors of
the SHA built. Source build was the only path.

**It built cleanly**: `dotnet build src/Stryker.CLI/... -c Release` → **0 errors**, 327
warnings (all pre-existing nullability/`CS8632` noise in Stryker's own code). The
maintainers' 2026-08-14 note about *"some breaking changes and a blocking defect"* holding
up a release did not manifest as a build failure here.

> **Version banner — FIXED 2026-09-06.** The first packed build still printed
> `Version: 4.16.0` (only `PackageVersion` was overridden at pack time), making it
> indistinguishable from the released tool by its own banner. It now reads:
>
> ```
> Version: 0.0.0-BOGUS-LOCAL-master-55464f4-NOT-A-RELEASE
> ```
>
> Set via `VersionPrefix`/`VersionSuffix` in the CLONE's `src/Directory.Build.props`
> (never in this repo). `0.0.0` cannot collide with any real release, the label names
> the master SHA it was built from, and `NOT-A-RELEASE` is unmissable in a scrollback.
>
> **Why the label had to be valid SemVer.** `StrykerCLI.cs:218` does
> `SemanticVersion.TryParse(version, out var currentVersion)` and, on failure, logs a
> warning and **returns early — printing no version line at all**. So an arbitrary
> "obviously fake" string would have SUPPRESSED the banner rather than shouting,
> which is strictly worse than the problem it was meant to fix. Prerelease
> identifiers may contain only alphanumerics and hyphens; this label obeys that, so
> it parses and is printed in full.

[pr3752]: https://github.com/stryker-mutator/stryker-net/pull/3752
[pr3769]: https://github.com/stryker-mutator/stryker-net/pull/3769

### THE CANARY — passed

**A mutant this repository's tests are known to kill came back `Killed`. The instrument
works.**

```
[01:09:32 INF] Time Elapsed 00:28:57
[01:09:32 INF] The final mutation score is 100.00 %

Killed:   1     Survived: 0     Timeout:  0     Errors:  0
```

The mutant: **`src/Guardrails.Cli/Ui/LiveTableRows.cs:140`, Statement mutation → `;`** —
i.e. delete `rows.Add(new TaskLiveRow(task.Id));`, so the live table emits no task rows at
all. Run with `--test-runner mtp` over a character-span-restricted scope isolating that one
statement.

**The control is what makes this conclusive.** The *same tool build*, the *same repo*, the
*same file*, differing only in `test-runner`:

| runner | mutants tested | Killed | Survived | verdict on `LiveTableRows.cs:140` |
|---|---|---|---|---|
| `vstest` (the default) | 12 | **0** | **12** | **Survived** — fabricated |
| `mtp` (carries PR #3752) | 1 | **1** | 0 | **Killed** — real |

Under `vstest`, master reproduces the 4.16.0 defect exactly, including the
`[ERR] It looks like the test coverage capture failed` line, within 3 seconds. Under `mtp`
the same statement-deletion mutant is killed. **The fix is real and it reaches us.**

Ground truth was re-established by hand on this worktree before Stryker was trusted with
the question. The original canary (`waves.Count == 0` → `!=`, planted by hand, CRLF
preserved) fails **12 of 47** tests in **415 ms** across `CollapseCompletedWavesTests`,
`JitBreakdownVisibilityTests` and `MidRunWaveSpliceTests` — reverted immediately after
measuring. (The doc above records 15-of-170 for the same mutant under the wider 15-class
filter; both are the same fact measured against different oracle subsets.)

> **Method note, stated because it deviates from the brief.** The brief said to plant the
> mutant by hand *and then run Stryker over it*. Those two steps cannot both be done to the
> same line: with the mutant planted, Stryker's own baseline run starts red and the mutant
> it would generate there is the *inverse* (`!=` → `==`), which restores correct behaviour.
> The gate's intent — *can this tool kill a mutant we know is killable?* — was executed as
> two separate measurements: hand-plant to establish ground truth (above), revert, then
> Stryker on a clean tree, checking the status of a known-killable mutant in that file.

### Three findings that change how this tool must be driven

**1. The canary mutant named in the recommendation above is no longer usable — it is now
classified `CompileError`.**

The `waves.Count == 0` → `!=` mutant at `LiveTableRows.cs:110` is reported by this build as
`CompileError: Mutant caused compile errors`. **That classification is wrong** — the same
edit, applied by hand to the same line, compiles with zero errors and fails 12 tests.

Two mutants sit on that one expression, and the report marks *both* `CompileError`:

| line | mutator | replacement | reported |
|---|---|---|---|
| 110 | Equality mutation | `waves.Count != 0` | `CompileError` — **but it compiles** |
| 110 | Linq method mutation (`Count()` → `Sum()`) | `waves.Sum` | `CompileError` — correctly |

The second one is a mutator misfire: `waves.Count` is the `IReadOnlyList<T>.Count`
**property, not a LINQ `Count()` call**, so the substitution yields `waves.Sum == 0` —
a method group compared to an int, which genuinely cannot compile. The inference (from the
two facts above, not from Stryker's logs, which name no safe-mode rollback for this file) is
that the rollback removes the enclosing mutated expression and takes the valid equality
mutant down with it.

This matters beyond one line. The previous run's recommendation was *"the
`waves.Count == 0` → `!=` mutant is a ready-made canary."* Had this run used it as the gate
and asked only *"is line 110 still Survived?"*, the answer would have been "no, it's a
compile error" — which is neither a pass nor a fail, and would have read as another broken
instrument. **`LiveTableRows.cs:140` (statement deletion) is the better canary**: no LINQ
sibling, no rollback exposure, and semantically unmissable.

**2. `test-case-filter` is silently ignored by the MTP runner — the cost lever is gone.**

Confirmed both in the source (`TestCaseFilter` is referenced only under
`Stryker.TestRunner.VsTest/`; nothing in `Stryker.TestRunner.MicrosoftTestPlatform/` reads
it) and in every MTP run here: discovery reports **1,258 tests**, never the filtered subset.
No warning is emitted. The MTP runner filters tests *only* by coverage-derived test UIDs.

The committed `stryker-config.json` is built around that filter — it is what cut the oracle
from 1,258 tests to 170 and made the run finishable. **Under the runner that actually
works, that config's stated cost model does not apply.** The config's scope comments remain
correct; its cost note does not.

**3. `mutate` line-spans are CHARACTER offsets, not line numbers.**

`"**/Ui/LiveTableRows.cs{138..142}"` matched **zero** mutants and Stryker reported
`0 total mutants will be tested`. `FilePattern.Parse` builds `TextSpan.FromBounds(start, end)`
over the file's character offsets. The working form for lines 138–142 of that file is
`{8416..8581}`.

Stryker did say *"unable to calculate a mutation score"* rather than inventing one, which is
the honest failure — but it is one character-class away from the exact trap the config
header warns about: **a scope that matches nothing reports a flawless clean sheet.**

### The cost model, measured — and why it does not work yet

The fix restores correctness. It does not make the run affordable.

| phase | measured |
|---|---|
| Stryker build from source (Release) | 28 s |
| Analysis + solution build | ~15 s |
| **Initial 1,258-test run + generating 7,914 mutants (MTP)** | **8 m 49 s – 14 m 25 s** |
| **Per mutant, `coverage-analysis: off`, uncontended** | **13 m 20 s** (measured on a 1-mutant run) |
| **Per mutant, same, with concurrency 2** | **~26 min each** — 12 mutants took 2 h 37 m 44 s of mutation phase |
| `coverage-analysis: perTest` capture | **abandoned at 33 min**, incomplete |

Two mechanisms break the economics, and they compound:

- **No oracle filter** (finding 2), so every mutant that is not skipped runs against all
  1,258 tests, dozens of which spawn real harness runs invoking `dotnet build` and
  `dotnet test`.
- **`perTest` coverage capture degenerates.** It walks the suite one test at a time, and on
  this repo it began emitting
  `Timed out waiting for coverage relay ack for test <hash>; marking as Dubious`
  at a steady ~10–12 per minute — 232 tests marked `Dubious` in 33 minutes, with the rate
  showing essentially *every* test timing out by the end. It was killed at that point, so
  no completion figure exists.

  `Dubious` is not free: `CoverageAnalyser` adds every dubious test to *every* mutant's
  covering set. A capture that mostly times out therefore collapses `perTest` back into
  "run the whole suite per mutant" — after paying for the capture. The relay is a
  memory-mapped-file handshake (`MutantControl.EnsureEpochMmf`), and its documented fallback
  on failure is exactly this: report `Dubious` rather than hang.

Scaling the measured throughput — 12 mutants in 2 h 37 m of mutation phase at concurrency 2
— the committed live-surface scope (369 mutants across three files) projects to roughly
**80 hours**, i.e. three-and-a-half days of continuous running. That is why the run below is
scoped to one file.

### The run that was made

Scope deliberately narrowed to `**/Ui/LiveTableRows.cs` — **the same 12 mutants the
`vstest` path reported as 12 fabricated survivors**, so the two runs are directly
comparable.

| | 4.16.0-equivalent (`vstest`) | master `55464f4` (`mtp`) |
|---|---|---|
| Mutants generated (whole Cli project) | 7,914 | 7,914 |
| CompileError | 1,484 | 1,484 |
| Ignored (block already covered) | 6 | 6 |
| Ignored (out of `mutate` scope) | 6,412 | 6,412 |
| **Tested** | **12** | **12** |
| Killed | **0** | **12** |
| Survived | **12** | **0** |
| Timeout | 0 | **0** |
| Errors | 0 | 0 |
| **Score** | **0.00 %** | **100.00 %** |
| Wall clock | 1 min 12 s | **2 h 52 m 39 s** |

The skip counts are identical down to the last mutant, so this is the same twelve mutants
judged twice by the same binary. Every one of them:

| # | line | mutator | replacement |
|---|---|---|---|
| 1 | 55 | String mutation | `$""` |
| 2 | 111 | Block removal | `{}` |
| 3 | 122 | Logical mutation | `!showAllTasks \|\| completedWaves.Contains(wave.Dir)` |
| 4 | 122 | Negate expression | `!(!showAllTasks && completedWaves.Contains(wave.Dir))` |
| 5 | 122 | LogicalNot to un-LogicalNot | `showAllTasks` |
| 6 | 124 | Statement mutation | `;` |
| 7 | 133 | Logical mutation | `… == 0 && breakdownWaves?…` |
| 8 | 133 | Negate expression | `!(… == 0 \|\| breakdownWaves?…)` |
| 9 | 133 | Equality mutation | `breakdownWaves?.Contains(wave.Dir) != true` |
| 10 | 133 | Boolean mutation | `false` |
| 11 | 135 | Statement mutation | `;` |
| 12 | 140 | Statement mutation | `;` |

### Triage: there are no survivors

**Survived: 0.** There is no survivor list to triage into genuine-gap / equivalent-mutant /
timeout-misreported, because none was produced. `Timeout: 0` and `Errors: 0` as well, so the
score is not inflated by timeouts being scored as kills — the failure mode that would have
made a 100 % as hollow as the earlier 0 %.

**So the adversarial check was pointed the other way.** After a run that fabricated twelve
survivors, the symmetric risk in a 100 % run is fabricated *kills*. Three of the twelve were
re-applied to the source by hand (bytes patched so CRLF survives), built normally, and run
against the 47-test three-class subset:

| # | mutant | hand-applied result |
|---|---|---|
| 3 | line 122, `&&` → `\|\|` | **Failed: 14, Passed: 33, Skipped: 0, Total: 47** (968 ms) |
| 11 | line 135, `rows.Add(new WavePhaseLiveRow(wave.Dir));` → `;` | **Failed: 5, Passed: 42, Skipped: 0, Total: 47** (1 s) |
| 1 | line 55, `KeyFor` → `$""` | **Failed: 1, Passed: 46, Skipped: 0, Total: 47** (1 s) |

All three kills are real. Each was reverted immediately after measurement.

> **A fourth attempt failed for an instructive reason.** Mutant 10 (line 133 → `false`)
> **cannot be hand-verified in this repo**: `if (false)` makes line 135 unreachable, and
> `TreatWarningsAsErrors` turns `CS0162: Unreachable code detected` into a build error.
> Stryker compiles mutants with its own Roslyn compilation, which does not honour the
> MSBuild warnings-as-errors setting — so **Stryker can test mutants that this repo's build
> policy forbids.** That is not a defect (the IL is what the tests would have run against),
> but it does mean hand-verification is not available for every mutant Stryker reports.

### What this run does and does not certify

**Certifies:**

- **The instrument works.** Master's MTP runner activates mutants against this repo's
  xunit.v3 test projects. Twelve for twelve, with three independently confirmed by hand.
- **`LiveTableRows.Plan` is genuinely well pinned.** Every operator, boolean, statement and
  block mutation the tool can express in that file is caught by the existing tests. That is
  a real, if narrow, quality result — the first one this exercise has produced.

**Does not certify:**

- **Anything outside `LiveTableRows.cs`.** `LiveRunObserver.cs` and `LiveNarrative.cs` — the
  other two files in the committed live-surface scope, and the ones the #635 defect actually
  lived in — were **not** run. That leaves 357 of the 369 in-scope mutants unmeasured.
- **That #372/#635 cannot come back.** Unchanged from the analysis above: Stryker's mutators
  cannot express a method-call substitution, and the `[CallerFilePath]` source-reading guard
  in `LiveNarrativeCompositeTests` is invisible to a tool that mutates IL.
- **A 100 % score here does not generalise.** Twelve mutants in one 147-line pure function is
  the easiest possible target: no I/O, no concurrency, no child processes.

### Recommendation — revised

**Do not schedule it. Do not put it on the PR path. Keep it as an on-demand, source-built
instrument, and do not spend more on it until upstream ships a release.**

What changed: the verdict is no longer *blocked-on-tooling*. The tool now measures something
true. What has **not** changed is the conclusion about scheduling — but the reason is now
cost rather than correctness:

- **2 h 52 m for twelve mutants in one file.** The committed live-surface scope is 369
  mutants; at this measured rate that is **~80 hours**, and the whole Cli project is not
  reachable at all.
- **The cost lever is gone under the runner that works** (finding 2), and the alternative
  lever (`perTest` coverage) degenerates into the `Dubious` storm. Until one of those is
  fixed, every mutant costs a full 1,258-test suite run.
- **Waiting is nearly free.** The fix is merged; only a release is missing. A source build is
  a few minutes of clone-and-pack, so re-testing when the next version ships costs almost
  nothing.

Concrete order of operations for whoever picks this up next:

1. **Wait for the release.** Re-test `perTest` when it lands — if the coverage relay stops
   timing out, per-mutant cost collapses from a full suite to a handful of tests, and this
   becomes an affordable on-demand tool.
2. **If it is wanted before then**, run it source-built, one file at a time, on demand, and
   budget hours. `**/Ui/LiveNarrative.cs` is the highest-value next file.
3. **Keep the canary gate, but change the canary.** The gate is vindicated — it is the only
   reason this run is trustworthy in either direction. Use **`LiveTableRows.cs:140`
   statement-deletion**, not the `waves.Count == 0` mutant the earlier recommendation named
   (finding 1). Assert on that mutant's *status*, never on the score.
4. **File two upstream issues** if they are not already known: the LINQ `Count() → Sum()`
   mutator firing on a `Count` *property* and taking valid sibling mutants down with it
   (finding 1), and `test-case-filter` being silently ignored by the MTP runner (finding 2).

### Reproducing this

```bash
git clone https://github.com/stryker-mutator/stryker-net.git <src>     # 55464f4
dotnet pack <src>/src/Stryker.CLI/Stryker.CLI/Stryker.CLI.csproj -c Release \
  -o <pkg> -p:PackageVersion=4.17.0-master-55464f4
dotnet tool install dotnet-stryker --version 4.17.0-master-55464f4 \
  --add-source <pkg> --tool-path <tool>        # --tool-path, NOT -g

# from the repo root; -O MUST point outside the repo
<tool>/dotnet-stryker -f <config> -O <out> --skip-version-check
```

The config must set `"test-runner": "mtp"`. **Everything in this document that reports a
fabricated result was produced with the default, `vstest`.**

> Still true, and it bit again here: **an aborted run leaves the MUTATED assembly in
> `tests/**/bin`.** Killing the `perTest` attempt left a 777,216-byte `Guardrails.Cli.dll`
> in place with the 577,024-byte original beside it as `.stryker-unchanged`. It was restored
> by hand and hash-checked against `src/Guardrails.Cli/bin/`. Always check after an abort.

### Verification gate for this follow-up

Clean `dotnet test Guardrails.sln -c Debug` (builds first, so nothing ran against a leftover
mutated assembly), after all mutation work and with the tree restored:

| suite | passed | failed | skipped | total | duration |
|---|---|---|---|---|---|
| `Guardrails.Core.Tests` | 2,577 | **0** | 0 | 2,577 | 7 m 53 s |
| `Guardrails.Integration.Tests` | 1,254 | **0** | **4** | 1,258 | 17 m 24 s |
| **total** | **3,831** | **0** | **4** | **3,835** | — |

The 4 skips are named rather than summarised, because a skip is evidence that did not get
collected — and they are the same four as the run above, i.e. the opt-in real-Claude tests
(`GUARDRAILS_REAL_CLAUDE=1`) and their model-tiering siblings:

- `RealClaudeSmokeTests.TrivialPromptTask_RunsGreen_AgainstRealClaude`
- `RealClaudeClaudeNestedPlanTests.PromptActionTask_ClaudeNestedPlan_WorktreeMode_RunsGreen_AgainstRealClaude`
- `ModelTiering.NoRoutingGoldenTests.FreshBreakdown_ReproducesTheGoldenByteForByte`
- `ModelTiering.NoRoutingNegativeAssertionTests.FreshBreakdown_CarriesNoTierArtefacts`

`WebhookEventSinkTests.AFaultedPumpIsReportedNotSummarizedAsZero` (secondary finding 4
above) passed again here — further support for load-sensitivity rather than a defect.

**Tree state:** no source file was modified. Four mutants were hand-applied during this
session (three verifications plus the ground-truth canary) and each was reverted with
`git checkout --` immediately after its measurement. The only tracked change on this branch
from this session is this document. No `*.stryker-unchanged`, `StrykerOutput/` or
`.stryker-tmp/` remains anywhere in the worktree; all Stryker output went outside the repo
via `-O`, and the Stryker source clone, package and tool path all live outside it too.

**Configs are unchanged.** `stryker-config.json` and `stryker-config.core-execution.json`
were deliberately not edited: their do-not-trust headers are still accurate for the
*released* tool, which is what anyone running `dotnet stryker` from NuGet will get. The
working configuration differs from them in ways this document states (`"test-runner": "mtp"`,
and `test-case-filter` having no effect), and the runs above were driven from throwaway
configs outside the repo rather than by mutating the committed ones.
