# catches: a Lite Graph.psm1 whose diagnostics DIVERGE from Core's PlanValidator on the graph code set
#          (GR2001 GR2007 GR2010 GR2011 GR2019 GR2021 GR2041) - a missed code, a code Core does not emit,
#          GR2041 on writeScope [], GR2021 on a scope Core accepts, or a dispatcher that never runs the module. The --filter names this pair's OWN test class,
#          never the bare plan-wide trait (#455). Re-emits the failure block at the END so the WHY reaches
#          the retry-feedback tail (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateGraphTests'   # SAME string as task 05's census
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
    Write-Output "LiteValidateGraphTests failing - Lite's graph-set diagnostics diverge from Core's (see failure details above); fix scripts/lite/validate/Graph.psm1 (or the dispatcher's rule-module path), never the tests"
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
