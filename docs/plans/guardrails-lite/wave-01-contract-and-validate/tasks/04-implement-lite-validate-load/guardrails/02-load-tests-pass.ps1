# catches: a Lite validate.ps1 / Load.psm1 whose diagnostics DIVERGE from Core's PlanLoader+PlanValidator
#          on the load code set - a missed code, a code Core does not emit, semantic codes emitted over a
#          broken load, the wrong exit code or JSON shape. The --filter names this pair's OWN test class,
#          never the bare plan-wide trait (#455). Re-emits the failure block at the END so the WHY reaches
#          the retry-feedback tail (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateLoadTests'   # SAME string as task 03's census
# NO -v q on the TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block (#462).
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (forward form, #455).
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
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    Write-Output "LiteValidateLoadTests failing - Lite's load-set diagnostics diverge from Core's (see failure details above); fix scripts/lite/validate.ps1 or validate/Load.psm1, never the tests"
    exit 1
}

# ZERO-MATCH GUARD on the EXECUTED count (Passed+Failed; Total: would count skipped tests).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test was skipped (is pwsh on PATH?)."
    exit 1
}
exit 0
