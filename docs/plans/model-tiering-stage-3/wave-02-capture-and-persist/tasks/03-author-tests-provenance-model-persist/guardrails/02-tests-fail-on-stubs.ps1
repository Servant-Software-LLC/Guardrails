# catches: a HOLLOW test passing itself off as TDD red by hiding behind a genuinely-failing sibling
#          (#375). `dotnet test` exits non-zero if ANY selected test fails, so an `Assert.NotNull` pinned
#          to one of the three enumerated behaviours passes a suite-level exit-code check while proving
#          nothing. This is the PER-TEST CENSUS: every enumerated behaviour is bound to a pinned method
#          name and must be observed `Failed` in the runner's own TRX result file - never stdout (#248),
#          never `--list-tests` name discovery.
#
#          The census is worth more here than anywhere else in this wave, because the cheapest wrong
#          "both paths" test is one that asserts the SAME path twice.
#
#          What it does NOT prove: it proves each test is COUPLED to the code path (it fails while the
#          member is unpopulated), not that its assertion is correct. An invoking-then-hollow test is red
#          here, green after task 04, and PASSES this census. Closing that needs mutation testing; until
#          then the wrong-assertion residual is a human read.
#
# INVERSE polarity: non-zero from `dotnet test` is SUCCESS here, so the zero-match guard runs FIRST -
# a crash, or a Category trait that selected nothing, must never be certified as TDD red (#455).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The three behaviours the action prompt enumerates. The two regression tests
# (ProvenanceModel_StaysTheResolvedRoute_*, NoResolvedModelKeyIsEverWritten) are deliberately ABSENT:
# they are green from the start by design.
# MEASURED BASELINE 2026-08-23: zero of these names, and zero occurrences of the
# ObservedModelProvenance category, appear anywhere under tests/ on the entry tree.
$required = @(
    'ObservedModel_BecomesProvenanceModel_OnBothRecordPaths',
    'RequestedModel_IsWritten_WhenTheObservedDiffersFromTheRoute',
    'RequestedModel_IsAbsent_WhenTheObservedMatchesTheRequest'
)

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    # Category=ObservedModelProvenance, NOT the class name: the tests live inside the shipped
    # Stage2ConformanceTests, so a class filter would select ~2,100 lines of pre-existing green
    # assertions and this census would read their PASSES as this task's failure to be red.
    # NO -v q on a TEST command (#179).
    $out = dotnet test tests/Guardrails.Integration.Tests --nologo `
        --filter "Category=ObservedModelProvenance" `
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

    # ZERO-MATCH GUARD (#455), FIRST because the polarity is inverse.
    if ($results.Count -lt 1) {
        Write-Output "the filter Category=ObservedModelProvenance selected ZERO tests - the new methods are missing, or they do not carry [Trait(`"Category`", `"ObservedModelProvenance`")] at METHOD level. Nothing was measured."
        exit 1
    }

    $failures = @()
    foreach ($name in $required) {
        $matched = @($results | Where-Object { $_.testName -like "*$name*" })
        if ($matched.Count -lt 1) {
            $failures += "'$name' was not executed at all - the prompt pins this method name and the census reads it. Either the test is missing, or it is missing the ObservedModelProvenance category trait"
        }
        elseif (@($matched | Where-Object { $_.outcome -eq 'Failed' }).Count -lt 1) {
            $failures += "'$name' ran but did NOT fail (outcome: $(($matched | ForEach-Object { $_.outcome }) -join ', ')) - a TDD red must FAIL while ActionRun.ObservedModel is unpopulated and nothing folds it onto provenance. Either the assertion is hollow, or it asserts something already true"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== per-test red census: $($failures.Count) of $($required.Count) enumerated behaviours are not proven red ==="
        $failures | ForEach-Object { Write-Output "  - $_" }
        Write-Output "Every behaviour the prompt enumerates must be a NAMED test that FAILS against run.json today - where provenance.model is still the model the harness ASKED for."
        exit 1
    }
    exit 0
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
