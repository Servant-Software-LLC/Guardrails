# catches: a parity "fix" that edits Load.psm1 / Graph.psm1 / validate.ps1 (all inside this task's
#          writeScope) and breaks the ANCESTOR pairs' tests - LiteValidateLoadTests and
#          LiteValidateGraphTests, already green at tasks 04/06. Guardrail 02 cannot see that regression:
#          its filter names only this pair's classes (#455). These classes belong to ANCESTORS of this task
#          (never to a downstream task), so widening to them cannot create the #455 forward deadlock.
#          The substring 'Lite.LiteValidate' selects LiteValidateLoad/Graph/SubsetTests and NOT
#          LiteValidationParityTests ('LiteValidati...' does not contain 'LiteValidate').
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidate'
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    $detail = @()
    $emit = $false
    foreach ($line in $out) {
        if ("$line" -match '^\s*Failed\s+\S' -or "$line" -match '^\s*Error Message:') { $emit = $true }
        elseif ("$line" -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - inspect the full log above)" }
    Write-Output "an earlier LiteValidate* class regressed - your change to Load.psm1/Graph.psm1/validate.ps1 broke a behaviour tasks 04/06 already proved. Satisfy parity AND those tests; they do not conflict unless a test is wrong (then needsHuman)."
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the --filter '$filter' matched nothing, is malformed, or every match is [Skip]ped (pwsh missing?)."
    exit 1
}
exit 0
