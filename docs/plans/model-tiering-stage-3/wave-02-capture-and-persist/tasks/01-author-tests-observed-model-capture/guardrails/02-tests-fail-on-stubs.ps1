# catches: a HOLLOW test passing itself off as TDD red by hiding behind a genuinely-failing sibling
#          (#375). `dotnet test` exits non-zero if ANY selected test fails, so an
#          `Assert.True(true)` pinned to one of the four enumerated behaviours passes a suite-level
#          exit-code check while proving nothing. This is the PER-TEST CENSUS: every enumerated
#          behaviour is bound to a pinned method name and must be observed `Failed` in the runner's own
#          TRX result file - never stdout (#248), never `--list-tests` name discovery (a hollow body
#          satisfies "a test with this name exists" exactly as a comment satisfies a token floor).
#
#          What it does NOT prove, stated so nobody reads a green as more than it is: it proves each
#          test is COUPLED to the code path (it fails while the member is unpopulated), not that its
#          assertion is correct. An invoking-then-hollow test - `var r = Parse(s); Assert.NotNull(r);` -
#          is red here, green after task 02, and PASSES this census.
#
# INVERSE polarity: non-zero from `dotnet test` is SUCCESS here, so the zero-match guard runs FIRST -
# a crash or a filter that selected nothing must never be certified as TDD red (#455).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The four behaviours the action prompt enumerates, each bound to its pinned method name. The two
# regression tests (NoModelAnywhere_*, PromptResultObservedModel_*) are deliberately ABSENT from this
# table: they are green from the start by design.
# MEASURED BASELINE 2026-08-23: zero of these names appear anywhere under tests/ on the entry tree.
$required = @(
    'InitLine_Model_IsCaptured',
    'ResultLine_Model_IsTheFallback_WhenInitCarriedNone',
    'InitModel_Wins_OverADifferingResultLineModel',
    'ClaudePromptRunner_CarriesTheObservedModel_OffARealStream'
)

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    # NO -v q on a TEST command (#179).
    $out = dotnet test tests/Guardrails.Core.Tests --nologo `
        --filter "FullyQualifiedName~ObservedModelCaptureTests" `
        --logger "trx;LogFileName=census.trx" --results-directory $tmp 2>&1
    $out | ForEach-Object { Write-Output $_ }

    $trx = Get-ChildItem -Path $tmp -Filter '*.trx' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not a census failure: with no result file every clause below would report
        # "unbound" and blame the tests for a run that never happened.
        Write-Output "no .trx was produced under $tmp - the test RUN did not happen (build failure, host crash, or a malformed --filter). This is not a verdict about the tests; read the log above."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -Path $trx.FullName
    $results = @($doc.TestRun.Results.UnitTestResult)

    # ZERO-MATCH GUARD (#455), FIRST because the polarity is inverse: a --filter that selects nothing
    # makes `dotnet test` exit 0 with an empty TRX, which would otherwise read as "nothing failed".
    if ($results.Count -lt 1) {
        Write-Output "the filter FullyQualifiedName~ObservedModelCaptureTests selected ZERO tests - the class is missing, misnamed, or in a project this filter does not reach. Nothing was measured."
        exit 1
    }

    $failures = @()
    foreach ($name in $required) {
        $matched = @($results | Where-Object { $_.testName -like "*$name*" })
        if ($matched.Count -lt 1) {
            $failures += "'$name' was not executed at all - the prompt pins this method name and the census reads it; add the test, or it is not covered"
        }
        elseif (@($matched | Where-Object { $_.outcome -eq 'Failed' }).Count -lt 1) {
            $failures += "'$name' ran but did NOT fail (outcome: $(($matched | ForEach-Object { $_.outcome }) -join ', ')) - a TDD red must FAIL against the unpopulated stub. Either the assertion is hollow, or it asserts something already true"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== per-test red census: $($failures.Count) of $($required.Count) enumerated behaviours are not proven red ==="
        $failures | ForEach-Object { Write-Output "  - $_" }
        Write-Output "Every behaviour the prompt enumerates must be a NAMED test that FAILS against ClaudeResult.Model / PromptResult.ObservedModel while nothing populates them."
        exit 1
    }
    exit 0
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
