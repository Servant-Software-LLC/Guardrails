# catches: a DECLARED-EXEMPT census row quietly ceasing to exist, and a pinned row being deleted rather
#          than turned green. Task 17's red census excuses two names from being Failed (a correct
#          implementation leaves them green) but NOT from EXISTING; this is the other half of that
#          bargain - every pinned behaviour, exempt or not, observed Passed in the runner's own TRX once
#          the implementation has landed.
#
#          The row that most needs it is FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt.
#          It is the never-weaker floor of design 41 §6 - "a run that supplied nothing keeps a
#          byte-identical halt" - and it is the ONE row an implementation that discloses unconditionally
#          would have to delete in order to go green. 01-tests-pass cannot see that, because a deleted
#          test passes vacuously, and the red census that declared it exempt has already run by then.
#
#          FORWARD polarity, and its boundary stated: a forward census cannot see a hollow body (a hollow
#          test passes). What it CAN see is a test that was never written, one that was deleted, and one
#          that no longer runs. The hollow case is task 17's red census.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# SAME string as this pair's other half and as task 17's - copied verbatim.
$filter = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedTerminalGateHaltTests'

# Every name task 17's manifest pinned, pinned and exempt alike. All must now be Passed.
$pinned = @(
    'FailedTerminalGate_AfterASupply_NamesTheSupplierAndShaInTheHaltHeadline',
    'FailedTerminalGate_AfterARefresh_NamesTheRefreshInTheHaltHeadline',
    'FailedTerminalGate_WritesOneDetailLinePerUnauthoredRecord',
    'FailedTerminalGate_ListsEveryRecordOldestFirst_AcrossBothSections',
    'FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt',
    'PassingTerminalGate_AfterASupply_WritesNoHaltAtAll'
)

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr41-tg-fwd-census-" + [guid]::NewGuid().ToString('N'))
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

try {
    $out = & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
        --filter $filter --logger "trx;LogFileName=census.trx" --results-directory $resultsDir 2>&1 | Out-String
    Write-Output $out

    # PRECONDITION, not an unbound-behaviour report: "the run did not happen" is a different fact from
    # "the tests did not behave as required", and must read as one.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        Write-Output "PRECONDITION: no .trx under $resultsDir - the test run did not happen (a build break, a wrong project path, or a malformed --filter, which exits 0 with no results). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    # DOTTED navigation (the TRX has a default xmlns). The Where-Object is what lets the guard below fire:
    # with zero tests executed there is no <Results> element, the navigation yields $null, and
    # @($null).Count is 1, so the bare @(...) form would evaluate 1 -lt 1 and never fire.
    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $recorded = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($recorded.Count -lt 1) {
        Write-Output "PRECONDITION: the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped. A zero-match filter exits 0 and certifies nothing."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        # -cmatch and the (\(|$) tail: C# names are case-sensitive, and the tail admits a [Theory] row's
        # appended data without admitting a longer sibling name.
        $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
        $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
        if ($hits.Count -lt 1) {
            $failures += "[$name] NOT FOUND in the TRX. Task 17's prompt pins a behaviour to that exact method name and this task may not delete it. If it was removed because the implementation could not satisfy it, that is the finding - restore the test and fix the implementation instead."
            continue
        }
        $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
        if ($notGreen.Count -gt 0) {
            $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "[$name] outcome was '$seen', expected 'Passed'. ('NotExecuted' = [Fact(Skip=...)] - a skipped row is not a passing one.)"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== forward census FAILED ($($failures.Count) of $($pinned.Count) pinned behaviour(s) not observed Passed) ==="
        $failures | ForEach-Object { Write-Output "  - $_" }
        exit 1
    }

    Write-Output "Forward census: all $($pinned.Count) pinned behaviour(s) observed Passed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $resultsDir -ErrorAction SilentlyContinue
}
