# catches: a HOLLOW subset/parity test - named for the behaviour, body a tautology (Assert.True(true), an
#          assertion on the fixture the test itself wrote, a parity row that compares Core to Core). It
#          PASSES against the Subset.psm1 / validate.ps1 stubs and hides behind its genuinely-failing
#          siblings, so a suite-level non-zero exit would certify the file honest (#375). Every enumerated
#          behaviour - each GR2090 trigger, both GR2025 cases, each pinned parity ROW and the non-vacuity
#          check - is bound to the method (or theory row) the action prompt PINNED and must be observed
#          Failed in the runner's OWN TRX, never merely discovered by name.
# does NOT catch: a test that can NEVER pass (#530) - red is this gate's success condition, so a test red
#          for a reason no implementation can remove (e.g. a fixture the HARNESS itself rejects with an
#          error, making Subset_CleanLitePlan_PassesWithNoGR2090's exit-0 unreachable) reads here exactly
#          like one red for the right reason. That bill lands on task 08.
# DECLARED EXEMPTION: 'Parity_CorpusIsNonEmpty' never runs Lite - it asserts the MemberData corpus walk
#          found >= 8 rows including both examples - so a CORRECT test is GREEN on the stub tree. It
#          asserts Expect='Executed' (ran, not [Skip]ped). It stays in the manifest so a vacuous corpus
#          walk cannot be dropped silently.
# Culture pin: the census reads the TRX (schema tokens, not localized); kept so the log is readable.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=Lite&(FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateSubsetTests|FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidationParityTests)'

# THE MANIFEST. A bare string = a [Fact] method expected Failed. A hashtable with Row = one [Theory] row of
# Parity_LiteMatchesCore, matched on its TRX name `Parity_LiteMatchesCore(fixture: "<Row>")`. Expect =
# 'Executed' declares an exemption (see header). Names are the ones the action prompt pins.
$manifest = [ordered]@{
    'waved layout -> GR2090 error'                    = 'Subset_WavedLayout_IsError'
    'autonomy block -> GR2090 error'                  = 'Subset_AutonomyBlock_IsError'
    'routing block -> GR2090 error'                   = 'Subset_RoutingBlock_IsError'
    'gateway baseUrl -> GR2090 error'                 = 'Subset_GatewayBaseUrl_IsError'
    'overwatch profile -> GR2090 error'               = 'Subset_OverwatchProfile_IsError'
    'ai-merge profile -> GR2090 error'                = 'Subset_AiMergeProfile_IsError'
    'maxParallelism > 1 -> GR2090 warning'            = 'Subset_MaxParallelismAboveOne_IsWarning'
    'tiering block -> GR2090 warning'                 = 'Subset_TieringBlock_IsWarning'
    'maxCostUsd -> GR2090 warning'                    = 'Subset_MaxCostUsd_IsWarning'
    'stallTimeoutSeconds -> GR2090 warning'           = 'Subset_StallTimeoutSeconds_IsWarning'
    'missing profile -> GR2090 warning'               = 'Subset_MissingProfile_IsWarning'
    'clean lite plan -> exit 0, no GR2090'            = 'Subset_CleanLitePlan_PassesWithNoGR2090'
    'missing review marker -> GR2025 warning'         = 'ReviewMarker_Missing_EmitsGR2025Warning'
    'fresh Core-written marker -> no GR2025'          = 'ReviewMarker_FreshFromCore_EmitsNoGR2025'
    'invalid-* fixtures non-vacuous on both sides'    = 'Parity_InvalidFixtures_AreNonVacuous'
    'parity row: validate-subset/clean-lite'          = @{ Name = 'Parity_LiteMatchesCore'; Row = 'validate-subset/clean-lite' }
    'parity row: validate-subset/no-profile'          = @{ Name = 'Parity_LiteMatchesCore'; Row = 'validate-subset/no-profile' }
    'parity row: validate-subset/max-parallelism-4'   = @{ Name = 'Parity_LiteMatchesCore'; Row = 'validate-subset/max-parallelism-4' }
    'parity row: validate-subset/invalid-unknown-dependency' = @{ Name = 'Parity_LiteMatchesCore'; Row = 'validate-subset/invalid-unknown-dependency' }
    'parity row: validate-subset/invalid-no-guardrails'      = @{ Name = 'Parity_LiteMatchesCore'; Row = 'validate-subset/invalid-no-guardrails' }
    'parity row: validate-subset/invalid-cycle'       = @{ Name = 'Parity_LiteMatchesCore'; Row = 'validate-subset/invalid-cycle' }
    'parity row: examples/hello-guardrails'           = @{ Name = 'Parity_LiteMatchesCore'; Row = 'examples/hello-guardrails' }
    'parity row: examples/parallel-hello'             = @{ Name = 'Parity_LiteMatchesCore'; Row = 'examples/parallel-hello' }
    # DECLARED EXEMPTION - never runs Lite, green on the stub tree when correct (see header).
    'corpus walk is non-empty (exemption)'            = @{ Name = 'Parity_CorpusIsNonEmpty'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-lite07-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
# No -v q: nothing is re-emitted here, and the flag propagates onto forward checks by cloning (#462).
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the one legitimate early exit. No TRX means the run never happened.
# Test-Path FIRST, then -LiteralPath: on a MISSING $resultsDir, `Get-ChildItem <missing> -Filter -Recurse`
# falls back to walking the parent (all of %TEMP%) and hangs - in exactly the never-ran case this diagnoses.
$trx = $null
if (Test-Path -LiteralPath $resultsDir) {
    $trx = Get-ChildItem -LiteralPath $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
}
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, the build failed, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation (the TRX has a default xmlns). Where-Object is load-bearing: with zero tests executed
# there is no <Results> element and @($null).Count is 1, which would make the guard below never fire.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $entry  = $manifest[$behaviour]
    $name   = if ($entry -is [string]) { $entry } else { $entry.Name }
    $row    = if ($entry -is [string]) { $null } else { $entry.Row }
    $expect = if ($entry -is [string] -or -not $entry.Expect) { 'Failed' } else { $entry.Expect }
    # -cmatch: C# names are case-SENSITIVE. A [Fact] anchors on (\(|$); a theory ROW anchors on its data.
    $pattern = if ($row) { '\.' + [regex]::Escape($name) + '\(fixture: "' + [regex]::Escape($row) + '"\)' }
               else      { '\.' + [regex]::Escape($name) + '(\(|$)' }
    $hits = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $what = if ($row) { "theory row '$name(fixture: `"$row`")'" } else { "test '$name'" }
        $failures += "$behaviour -> no $what ran (absent from the file, a differently-spelled id/parameter name, a missing fixture, or not selected by the filter)"
        continue
    }
    if ($expect -eq 'Executed') {
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed', see this file's header) and did NOT execute. An exempt row still has to run."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name$(if ($row) { "($row)" })' is $seen on the STUB tree, not Failed. Against the stubs validate.ps1 prints `"stub`":true and emits no diagnostics; a test that still passes never checks the script's own output. Assert script=='validate', no stub flag, and the codes."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the stubs ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
