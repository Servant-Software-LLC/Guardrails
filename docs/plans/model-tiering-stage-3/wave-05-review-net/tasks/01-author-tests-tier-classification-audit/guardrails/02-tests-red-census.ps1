# catches: a HOLLOW test passing itself off as TDD red by hiding behind a genuinely-failing sibling
#          (#375). `dotnet test` exits non-zero if ANY selected test fails, so an `Assert.True(true)`
#          pinned to one of the behaviours below passes a suite-level exit-code check while proving
#          nothing. This is the PER-TEST CENSUS: every behaviour is bound to a pinned method name and
#          its outcome is read from the runner's own TRX result file - never stdout (#248), never
#          `--list-tests` name discovery (a hollow body satisfies "a test with this name exists"
#          exactly as a comment satisfies a token floor).
#
#          THREE groups, three different required outcomes, and the split is the whole point of this file.
#
#          (A) The audit's behaviour - must be observed FAILED. These call TierClassificationAudit, which
#          is a throwing stub at this task, so a green one is either hollow or asserts something already
#          true. The sharpest of them is
#          PlanWideDefaultTier_DoesNotDischargeTheFinding_BecauseItIsResolvedAtLoad: the loader resolves
#          tiering.defaultTier into EVERY untagged task, so an audit that read the resolved tier would find
#          nothing forever, and that test is the only thing standing between this wave and that silence.
#
#          (B) The graceful skip - DELIBERATELY EXCLUDED, and the exclusion is the honest half of this
#          check. LegacyPlan_WithNoTierVocabularyAnywhere_ProducesNothingAtAll and
#          RemovingOnlyTheTieringMetadata_SilencesTheFinding_TheTagsAreUntouched are SILENCE assertions: a
#          legacy folder produces no finding both before and after the feature exists. Against a throwing
#          stub they happen to be red, but for the wrong reason entirely - the stub throws rather than the
#          plan being judged - so requiring them here would encode exactly the confusion that nearly
#          destroyed the Invariant-7 test in wave 1. They are not unguarded: they are asserted alongside
#          the positive cases and 02-implement-tier-classification-audit's tests-pass guardrail runs them,
#          which is the only place their green means anything.
#
#          (C) The fixtures' own integrity - must be observed PASSED. TheTwoFixturesDifferOnlyInTheMissingTag
#          and BothFixturesLoadAndValidateClean_BecauseValidateCannotSeeThisDefect never call the audit, so
#          they are green the moment the fixtures are authored correctly. Requiring them PASSED closes the
#          hole this census would otherwise leave wide open: without it, a test that failed because the
#          fixture is MISSING or unloadable would be counted as a clean red, and the whole wave would be
#          built on fixtures nobody had proven load.
#
#          What it does NOT prove: it proves each Group A test is COUPLED to the audit (it fails while the
#          audit throws), not that its assertion is correct. An invoking-then-hollow test is red here,
#          green after 02, and PASSES this census. That residual is a human read.
#
# SCOPE (#455): ONE class, in one project. `TierClassificationAuditTests` is a substring of no other test
# class anywhere under tests/ (verified 2026-08-24: neither `TierClassification` nor the full name occurs
# in the tree at all). Every Group A test is made green by 02-implement-tier-classification-audit, a task
# DOWNSTREAM of this one, so no sibling's tests could satisfy this red for us and this check waits on no
# descendant.
#
# INVERSE polarity for Group A: non-zero from `dotnet test` is SUCCESS here, so the zero-match guard runs
# FIRST - a crash, or a filter that selected nothing, must never be certified as TDD red (#455).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# MEASURED BASELINE 2026-08-24: none of these names appear anywhere under tests/ on the entry tree.
$mustFail = @(
    'ConfiguredPlan_FullyTagged_ProducesNoFinding',
    'ConfiguredPlan_UntaggedPromptTask_IsAFindingThatNamesTheTask',
    'PlanWideDefaultTier_DoesNotDischargeTheFinding_BecauseItIsResolvedAtLoad',
    'ScriptActionTask_IsNeverFlagged_ItRunsNoModel',
    'PinnedTask_IsNotFlagged_WhetherThePinIsModelRunnerOrEffort',
    'UntaggedJudge_IsAFindingOnlyWhenItHasNoClassifiedActorToFollow',
    'TheAuditNamesWhatItSaw_SoAnEmptyResultIsNotAVacuousOne'
)

$mustPass = @(
    'TheTwoFixturesDifferOnlyInTheMissingTag',
    'BothFixturesLoadAndValidateClean_BecauseValidateCannotSeeThisDefect'
)

$failures = @()
$filter = 'FullyQualifiedName~TierClassificationAuditTests'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    # NO -v q on a TEST command (#179).
    $out = dotnet test tests/Guardrails.Core.Tests --nologo --filter $filter `
        --logger "trx;LogFileName=census.trx" --results-directory $tmp 2>&1
    $out | ForEach-Object { Write-Output $_ }

    $trx = Get-ChildItem -Path $tmp -Filter '*.trx' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION: with no result file every clause below would report "unbound" and blame the tests
        # for a run that never happened.
        Write-Output ""
        Write-Output "no .trx was produced - the test RUN did not happen (build failure, host crash, or a malformed --filter). This is not a verdict about the tests; read the log above."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -Path $trx.FullName
    # `Where-Object { $_ }` is LOAD-BEARING, not tidiness. On a TRX with no results at all,
    # `$doc.TestRun.Results.UnitTestResult` is $null, and `@($null)` has Count 1 in PowerShell - so the
    # zero-match guard below would see one "result", never fire, and the empty run would be reported as
    # nine missing test names instead of as a filter that selected nothing. Measured against the entry
    # tree on 2026-08-24: that is exactly what the first draft did.
    $results = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    # ZERO-MATCH GUARD (#455), FIRST because Group A's polarity is inverse.
    if ($results.Count -lt 1) {
        Write-Output ""
        Write-Output "the filter $filter selected ZERO tests - the class is missing, empty, or named differently. Nothing was measured, so nothing is proven red."
        exit 1
    }

    foreach ($name in $mustFail) {
        $matched = @($results | Where-Object { $_.testName -like "*$name*" })
        if ($matched.Count -lt 1) {
            $failures += "'$name' was not executed at all - the prompt pins this method name and this census reads it. Either the test is missing, or it is not in TierClassificationAuditTests"
        }
        elseif (@($matched | Where-Object { $_.outcome -eq 'Failed' }).Count -lt 1) {
            $failures += "'$name' ran but did NOT fail (outcome: $(($matched | ForEach-Object { $_.outcome }) -join ', ')) - a TDD red must FAIL while TierClassificationAudit throws NotImplementedException. Either the assertion is hollow, or the test never calls the audit at all"
        }
    }

    foreach ($name in $mustPass) {
        $matched = @($results | Where-Object { $_.testName -like "*$name*" })
        if ($matched.Count -lt 1) {
            $failures += "'$name' was not executed at all - it is the fixtures' own integrity check and this census requires it. Without it, a Group A test that failed because a FIXTURE is missing or unloadable would be counted as a clean red"
        }
        elseif (@($matched | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) {
            $failures += "'$name' did not PASS (outcome: $(($matched | ForEach-Object { $_.outcome }) -join ', ')) - it never calls the audit, so it is green as soon as the fixture pair is correct. A red here means the fixtures themselves are wrong: they are not a discriminating pair, or one of them does not load, or the validator has something to say about a plan this wave asserts it cannot see"
        }
    }
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) problem(s) across $($mustFail.Count) red + $($mustPass.Count) green behaviour(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The two graceful-skip tests are EXCLUDED from this census on purpose - a silence assertion cannot be red before the feature exists, and 02-implement-tier-classification-audit is where their green means something. Do not add them here to make this pass."
    exit 1
}
exit 0
