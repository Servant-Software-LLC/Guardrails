# catches: a HOLLOW wiring test - one named for the behaviour whose body is a tautology
#          (Assert.True(true), an Assert.NotNull on a value the test itself constructed, any assertion
#          that never invokes the phase). It PASSES against the UNWIRED PlanPreflightPhase, so a
#          suite-level non-zero exit certifies the whole file honest as long as ONE sibling genuinely
#          fails (#375). One entry per enumerated behaviour in this task's action prompt, each observed
#          in the runner's OWN TRX - never merely discovered by name, which a hollow body satisfies
#          exactly as well as a real one.
#
#          MEASURED, 2026-08-29, and it is the reason this task exists at all: a
#          SampleVerifierWiringTests.cs carrying all five pinned method names, four bodies
#          `Assert.True(true)` and one real EvaluateAsync call on a no-preflights plan asserting
#          `Assert.True(proceed)`, exits 0 against task 05's source-shape wiring guardrail with ZERO
#          output. Before the 04/05 split, task 05 authored both its own tests and its own
#          implementation, so nothing in the plan could tell a wired phase from an unwired one. This
#          census is that discriminator.
#
# THE EXEMPT DISCRIMINATOR - declared, not dropped (#375).
#   EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound is expected to PASS here, and is censused with
#   Expect='Executed' instead of Expect='Failed'. The reason is structural, not a concession:
#     src/Guardrails.Cli/PlanPreflightPhase.cs -> `if (plan.PlanPreflights.Count == 0) { return true; }`
#   is the FIRST statement of EvaluateAsync, so on a plan with no <plan>/preflights/ folder the UNWIRED
#   phase already returns true. A test asserting `Assert.True(proceed)` there is a real call to the real
#   seam that legitimately passes today AND after task 05 lands. Demanding it be red would demand that a
#   correct implementation fail - the false-red class that dead-ends every attempt at needsHuman.
#   What that costs us, stated plainly: this ONE test's body is proven only to EXIST and EXECUTE, not to
#   be coupled to the code path. Its forward proof is task 05's guardrail 03, which requires it Passed
#   after the wiring lands - and the four Expect='Failed' rows below carry the anti-hollow weight.
#   It stays IN the manifest on purpose: an undeclared omission is indistinguishable from an oversight,
#   and the next reviewer would have to re-derive why five behaviours produced four assertions.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so unlike dotnet.md 4.3 the
# guard does not depend on it - keep it anyway so the logged summary is readable and the pair stays
# copy-pasteable. NO -v q anywhere: pointless here (nothing is re-emitted) and it propagates onto
# forward checks by cloning (#462).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=BacklogSlate&FullyQualifiedName~SampleVerifierWiringTests'   # SAME string as the pair's forward half (task 05 guardrail 03)
# ~SampleVerifierWiringTests is DISCRIMINATING (#455/#193): the only other class this plan authors whose
# name shares a prefix is SampleVerifierTests (tasks 01/02), which lives in a DIFFERENT project and does
# not contain this substring in either direction. MEASURED 2026-08-29 over src/ and tests/: zero
# pre-existing occurrences of "SampleVerifierWiringTests" anywhere (positive control on the same
# invocation: "PlanPreflightPhase" returns 10 hits, so the search reached the trees).

# NO --no-build, deliberately, and it was MEASURED that this matters (2026-08-29). With --no-build this
# census reads whatever type names are in bin/, not what is in the SOURCE tree: after a run in which
# SampleVerifierWiringTests.cs had existed and was then deleted, `--no-build` re-executed the FIVE
# STALE tests still compiled into Guardrails.Integration.Tests.dll and the census exited 0 over a
# source tree with no test file in it at all. In the normal ordering 02-build-passes refreshes the
# assembly first, so the window is narrow - but this is the load-bearing discriminator of the whole
# 04/05 split, a single-guardrail `revalidate` re-runs it out of order, and a check that can certify a
# deleted file is exactly the silent-in-the-direction-that-looks-fine failure this plan exists to end.
# The incremental build costs ~15s. Pay it.
$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-unwired-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=unwired.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# GUARD FIRST, exit code never consulted (#455, inverse polarity). This check's success condition is a
# per-test OUTCOME table, not a non-zero suite exit - so a crashed test host, which exits non-zero with
# no results, must NOT be allowed to read as "TDD red". The precondition below is the only early exit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, the project was never built so --no-build had nothing to run, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
$xml = [xml](Get-Content $trx.FullName -Raw)
# The `| Where-Object { $_ }` is LOAD-BEARING and is the fix for an inert guard that shipped in the
# sibling censuses. MEASURED on this box, 2026-08-29 (pwsh 7):
#     @($null).Count                                     -> 1
#     @([xml]'<TestRun/>'.TestRun.Results.UnitTestResult) -> Count 1   (one $null element)
#     @(...same... | Where-Object { $_ })                 -> Count 0
#   so the bare `@($xml.TestRun.Results.UnitTestResult).Count -lt 1` form can NEVER fire: a TRX with no
#   <Results> element yields $null, and @($null) is a one-element array. The filtered form fires.
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them. Check the class name (SampleVerifierWiringTests) and the trait (Category=BacklogSlate) against what was authored."
    exit 1
}

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it, plus
# the outcome required against the UNWIRED phase. Cross-checked BY HAND against
# tasks/04-author-tests-verifier-wiring/action.prompt.md - the prompt<->manifest agreement is NOT
# mechanically enforced (measured: validate exits 0 either way).
#   Expect = 'Failed'   -> must be observed Failed. A hollow body cannot satisfy this.
#   Expect = 'Executed' -> must be present and NOT [Skip]ped. The one declared exemption, above.
$manifest = @(
    @{ Behaviour = 'a reversed committed pair HALTS the pre-DAG phase'
       Test      = 'EvaluateAsync_ReturnsFalse_WhenACommittedSamplePairIsReversed'
       Expect    = 'Failed' }
    @{ Behaviour = 'it halts even for a plan with NO preflights/ folder (the placement trap)'
       Test      = 'EvaluateAsync_HaltsOnABadSamplePair_EvenWhenThePlanDeclaresNoPreflightsFolder'
       Expect    = 'Failed' }
    @{ Behaviour = 'a SOUND pair does not halt (the step is not unconditional)'
       Test      = 'EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound'
       Expect    = 'Executed' }
    @{ Behaviour = 'the halt names the failing pair in the journal (#432)'
       Test      = 'EvaluateAsync_JournalsTheFailingPair_SoAPostMortemReaderCanSeeWhichPairHalted'
       Expect    = 'Failed' }
    @{ Behaviour = 'the RUN stops before scheduling any task - zero attempts'
       Test      = 'Run_HaltsBeforeSchedulingAnyTask_WhenAPlansCommittedSamplePairIsReversed'
       Expect    = 'Failed' }
)

# ACCUMULATE (#478/#179): one distinguishable message per unbound behaviour, dumped once at the end, so
# ONE attempt learns every gap instead of discovering them one retry at a time.
$failures = @()
foreach ($row in $manifest) {
    $name = $row.Test
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$($row.Behaviour) -> no test named '$name' ran (absent from the file, or not selected by the filter '$filter'). Every one of the five pinned names must be present and carry [Trait(""Category"", ""BacklogSlate"")]."
        continue
    }

    if ($row.Expect -eq 'Failed') {
        $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
        if ($notRed.Count -gt 0) {
            $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "$($row.Behaviour) -> '$name' is $seen against the UNWIRED PlanPreflightPhase, not Failed. A test that does not fail against a phase which has never been taught to verify sample pairs never invokes that behaviour: it asserts a tautology and certifies nothing, and task 05 would go green having wired nothing. Call PlanPreflightPhase.EvaluateAsync and assert on what IT returned or journaled. ('NotExecuted' = [Fact(Skip=...)].) Note: asserting on SampleVerifier's own findings also lands here - task 02 already implemented it, so such a test PASSES today."
        }
        continue
    }

    # Expect = 'Executed' - the declared exemption. It is allowed to pass (and does), but it may not be
    # skipped or silently dropped: this is the only test standing between task 05 and a phase that
    # returns false unconditionally.
    $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' })
    if ($notRun.Count -gt 0) {
        $failures += "$($row.Behaviour) -> '$name' is NotExecuted ([Fact(Skip=...)]). This test is the DECLARED exemption from the red bar - it legitimately passes against the unwired phase because EvaluateAsync returns true at its first line for a plan with no preflights/ folder - but it must still RUN. Un-skip it."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test census against the UNWIRED phase: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Four of the five pinned tests MUST be observed Failed here; EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound is the one declared exception and must merely execute. Do NOT make it red, and do NOT weaken any of the other four to fit."
    exit 1
}
exit 0
