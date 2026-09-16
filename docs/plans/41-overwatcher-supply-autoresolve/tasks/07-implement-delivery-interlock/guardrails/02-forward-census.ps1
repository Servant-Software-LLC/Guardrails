# catches: a pinned behaviour quietly ceasing to exist, or ceasing to run. Its sibling red census
#          (task 06) proved each name was Failed on the stub tree; this is the other half of that
#          bargain - every pinned behaviour observed Passed in the runner's OWN TRX once the
#          implementation has landed.
#
#          FORWARD polarity, and its boundary stated: a forward census cannot see a hollow body (a
#          hollow test passes). What it CAN see is a test that was never written, one that was
#          renamed, and one that was silenced with [Fact(Skip=...)] - which the suite exit code
#          reports as success.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The SAME $filter string as the pair's red census, copied verbatim.
$filter = 'Category=OverwatchSupply&(FullyQualifiedName~RunOutcomePolicyTests|FullyQualifiedName~WaveScopedInterlockTests)'

# Every name task 06's manifest pinned. There were NO declared exemptions in that census, so every one
# of these was observed Failed on the stubs and must now be observed Passed.
$pinned = @(
    'SuppressesDelivery_True_WhenAutoSuppliedRecorded',
    'SuppressingDecision_ReturnsTheAutoSuppliedEntry_AsTheEvidence',
    'AutoSupplied_SuppressesDelivery_ButIsNotAnUnreviewedWave',
    'AutoApplied_StillDelivers_BecauseAutoSuppliedIsADifferentToken',
    'AnAutoSuppliedDecision_HoldsTheWaveBarrierDelivery',
    'AnAutoSuppliedDecisionOutsideAnyWave_HoldsEveryDelivery',
    'AnAutoSuppliedDecisionInAnUncoveredWave_DoesNotHoldThisDelivery',
    'BothSpellingsAgreeOnEveryToken_OverTheSameDecision'
)

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr41-fwd-07-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

$out = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
       --filter $filter --logger "trx;LogFileName=census.trx" --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

try {
    # PRECONDITION, not an unbound-behaviour report: "the run did not happen" is a different fact from
    # "the tests did not behave as required", and must read as one.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    # The Where-Object is what lets the guard below fire: with zero tests executed the TRX has NO
    # <Results> element, the dotted navigation yields $null, and @($null).Count is 1.
    $xml      = [xml](Get-Content $trx.FullName -Raw)
    $recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($recorded.Count -lt 1) {
        Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, is malformed, or every match is [Skip]ped. A zero-match filter exits 0 and certifies nothing."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
        $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
        $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
        if ($hits.Count -lt 1) {
            $failures += "[$name] NOT FOUND in the TRX - the prompt pins this behaviour to a test of that name; it was never executed. Do not rename or delete a pinned test to make this pass."
            continue
        }
        $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
        if ($notGreen.Count -gt 0) {
            $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "[$name] outcome was '$seen', expected 'Passed'. ('NotExecuted' means [Fact(Skip=...)] - a skipped test is invisible evidence loss, not a pass.)"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== forward census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output "  - $_" }
        exit 1
    }

    Write-Output "Forward census: all $($pinned.Count) pinned behaviour(s) observed Passed."
    exit 0
}
finally {
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
}
