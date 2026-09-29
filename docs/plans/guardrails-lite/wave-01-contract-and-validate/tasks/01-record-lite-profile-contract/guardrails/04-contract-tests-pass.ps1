# catches: an implementation whose behaviour deviates from the tests THIS task pair owns (LiteProfileContractTests (+ the DiagnosticCatalogueTests regression guard)).
#          The --filter names the pair's OWN class(es), never the bare plan-wide trait (#455). Beyond the
#          exit code, every pinned test must be observed PASSED in the runner's own TRX - so a test the
#          implementation "fixed" by getting it [Skip]ped, or renamed out of the filter, cannot go green.
#          Re-emits the assertion/exception block at the END so the WHY reaches the retry tail (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)
$project = 'tests/Guardrails.Core.Tests'
$filter  = '(Category=Lite&FullyQualifiedName~Guardrails.Core.Tests.Loading.LiteProfileContractTests)|FullyQualifiedName~Guardrails.Core.Tests.DiagnosticCatalogueTests'   # SAME string as the paired test-author task's census

$manifest = [ordered]@{
    'a guardrails.json carrying "profile":"lite" loads with no error diagnostics' = 'ProfileLite_LoadsWithNoErrorDiagnostics'
    'the profile key changes no validator diagnostic code vs. the unmarked plan' = 'ProfileLite_ValidatesToTheSameDiagnosticCodesAsTheUnmarkedPlan'
    'contrast: the same load path DOES report a malformed guardrails.json as GR1002' = 'ContrastCase_MalformedGuardrailsJson_IsReportedAsGR1002'
    'regression: the next-free marker still names a free code above the high-water mark' = 'TheNextFreeMarkerNamesACodeThatIsActuallyFree'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr-lite-pass-$PID"
Remove-Item -LiteralPath $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
# NO -v q on the TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block (#462).
$out = dotnet test $project --filter $filter --no-build --nologo --logger 'trx;LogFileName=pass.trx' --results-directory $resultsDir 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a host that never ran exits non-zero with no summary.
if ($testExit -ne 0) {
    $detail = @()
    $emit = $false
    foreach ($line in $out) {                              # BLOCK capture, not a line allowlist (#608)
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - inspect the full log above)" }
    Write-Output "LiteProfileContractTests (+ the DiagnosticCatalogueTests regression guard) failing - make the implementation satisfy the authored tests; do NOT edit the tests (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): key on the EXECUTED count (Passed+Failed), never Total: or a message string.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the --filter '$filter' matched no tests, is malformed, or every match is [Skip]ped. This guardrail certified nothing."
    exit 1
}

# PER-TEST PASS CENSUS: every pinned test present in the TRX and Passed (a Skip reads NotExecuted).
# Test-Path FIRST: Get-ChildItem on a MISSING path with -Recurse walks the parent (%TEMP%) and hangs.
if (-not (Test-Path -LiteralPath $resultsDir)) {
    Write-Output "tests exited 0 but no results directory ($resultsDir) was written - cannot confirm the pinned tests ran"
    exit 1
}
$trx = Get-ChildItem -LiteralPath $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "tests exited 0 but no .trx was written under $resultsDir - cannot confirm the pinned tests ran"
    exit 1
}
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name    = $manifest[$behaviour]
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> pinned test '$name' did not run (renamed, removed, or filtered out)"
        continue
    }
    $notPassed = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notPassed.Count -gt 0) {
        $failures += "$behaviour -> '$name' is $(($notPassed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/'), not Passed"
    }
}
if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== pass census: $($failures.Count) of $($manifest.Count) pinned tests are not Passed ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0