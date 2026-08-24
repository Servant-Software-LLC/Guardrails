# catches: a HOLLOW test passing itself off as TDD red by hiding behind a genuinely-failing sibling
#          (#375). `dotnet test` exits non-zero if ANY selected test fails, so an `Assert.True(true)`
#          pinned to one of the behaviours below passes a suite-level exit-code check while proving
#          nothing. This is the PER-TEST CENSUS: every behaviour is bound to a pinned method name and
#          must be observed `Failed` in the runner's own TRX result file - never stdout (#248), never
#          `--list-tests` name discovery (a hollow body satisfies "a test with this name exists"
#          exactly as a comment satisfies a token floor).
#
#          The end-to-end name in the second group is the one this census most needs to hold. Wave 3
#          already prints `[model] <task> attempt N: <model>` into --no-ui output, so an author who wrote
#          `Assert.Contains(model, output)` instead of isolating the `Models used:` line would have a test
#          that is GREEN on this wave's entry tree. It would then be reported here as "ran but did NOT
#          fail", by name, rather than sliding through behind its five failing siblings.
#
#          What it does NOT prove: it proves each test is COUPLED to the code path (it fails while the
#          aggregator throws and nothing prints the line), not that its assertion is correct. An
#          invoking-then-hollow test is red here, green after 02-implement-models-used-report, and PASSES
#          this census. That residual is a human read.
#
# ONE BEHAVIOUR IS DELIBERATELY EXCLUDED, and the exclusion is the honest half of this check.
# `Run_DeterministicPlan_OmitsModelsUsedLine` asserts an ABSENCE - that a script-only plan prints no
# models-used line - which is trivially true before the feature exists. It CANNOT be red on this tree, so
# requiring it here would make the census unsatisfiable and dead-end the task at needs-human. It is not
# unguarded: 02-implement-models-used-report's tests-pass guardrail runs it, and it is the only thing
# stopping that task from printing an empty `Models used:` line on every deterministic run.
#
# SCOPE (#455): each filter names exactly ONE class, in its own project. Both are made green by
# 02-implement-models-used-report - a task DOWNSTREAM of this one - so no sibling's tests could satisfy
# this red for us, and this check waits on no descendant. `ModelsUsedSummaryTests` and
# `ModelsUsedReportTests` are each a substring of no other test class in either project (verified
# 2026-08-23: neither `ModelsUsed` nor either full name occurs anywhere under tests/).
#
# INVERSE polarity: non-zero from `dotnet test` is SUCCESS here, so the zero-match guard runs FIRST -
# a crash, or a filter that selected nothing, must never be certified as TDD red (#455).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# MEASURED BASELINE 2026-08-23: zero of these names appear anywhere under tests/ on the entry tree.
$groups = @(
    @{
        Project = 'tests/Guardrails.Core.Tests'
        Class   = 'ModelsUsedSummaryTests'
        Names   = @(
            'Attempts_AcrossTasksAndRetriesCountPerModel',
            'AttemptsWithoutAModel_AreExcluded_WithNoBucketOfTheirOwn',
            'RunWithNoRecordedModel_SummarizesAndRendersNull',
            'RenderedLine_NamesEveryRecordedModel_WithAStrictlyPositiveCount',
            'RequestedModel_PresentOnlyOnMismatch_IsCarriedIntoTheSegment',
            'SegmentOrder_IsDeterministic_AndDoesNotShuffle'
        )
    },
    @{
        Project = 'tests/Guardrails.Integration.Tests'
        Class   = 'ModelsUsedReportTests'
        Names   = @(
            'Run_PromptPlan_PrintsModelsUsedLine_NamingTheModelTheJournalRecorded'
        )
    }
)

$failures = @()
$counted = 0

foreach ($g in $groups) {
    $filter = "FullyQualifiedName~$($g.Class)"
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-census-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try {
        # NO -v q on a TEST command (#179).
        $out = dotnet test $g.Project --nologo --filter $filter `
            --logger "trx;LogFileName=census.trx" --results-directory $tmp 2>&1
        $out | ForEach-Object { Write-Output $_ }

        $trx = Get-ChildItem -Path $tmp -Filter '*.trx' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $trx) {
            # PRECONDITION for this group: with no result file every clause below would report "unbound"
            # and blame the tests for a run that never happened.
            $failures += "no .trx was produced for $($g.Project) - the test RUN did not happen (build failure, host crash, or a malformed --filter). This is not a verdict about the tests; read the log above"
            continue
        }

        [xml]$doc = Get-Content -Raw -Path $trx.FullName
        $results = @($doc.TestRun.Results.UnitTestResult)

        # ZERO-MATCH GUARD (#455), FIRST because the polarity is inverse.
        if ($results.Count -lt 1) {
            $failures += "the filter $filter selected ZERO tests in $($g.Project) - the class is missing, empty, or named differently. Nothing was measured, so nothing is proven red"
            continue
        }

        foreach ($name in $g.Names) {
            $counted++
            $matched = @($results | Where-Object { $_.testName -like "*$name*" })
            if ($matched.Count -lt 1) {
                $failures += "'$name' was not executed at all - the prompt pins this method name and the census reads it. Either the test is missing, or it is not in $($g.Class)"
            }
            elseif (@($matched | Where-Object { $_.outcome -eq 'Failed' }).Count -lt 1) {
                $failures += "'$name' ran but did NOT fail (outcome: $(($matched | ForEach-Object { $_.outcome }) -join ', ')) - a TDD red must FAIL while JournalModelsUsed throws NotImplementedException and nothing prints a models-used line. Either the assertion is hollow, or it asserts something already true on this tree (for the end-to-end test that means asserting on the whole output instead of isolating the `Models used:` line - wave 3 already prints the attempt's model there)"
            }
        }
    }
    finally {
        Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) problem(s) across $counted enumerated behaviour(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output "Every behaviour this task's prompt enumerates - except the one absence test it names as excluded - must be a NAMED test that FAILS on this wave's entry tree."
    exit 1
}
exit 0
