# catches: a Subset.psm1 that emits the wrong GR2090 severity / no feature name, a GR2025 that disagrees
#          with the harness's ReviewMarker.Evaluate, or a validate.ps1 whose code set drifts from
#          Guardrails.Cli.PlanProbe.LoadAndValidate on any parity row - i.e. the tests THIS task pair owns
#          (task 07's two classes; same --filter as its census, copied verbatim). Re-emits the failure
#          block at the END so the WHY reaches the retry-feedback tail (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=Lite&(FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateSubsetTests|FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidationParityTests)'
# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual block the re-emit needs (#179).
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a host that never ran exits non-zero with no summary.
if ($testExit -ne 0) {
    $detail = @()
    $emit = $false
    foreach ($line in $out) {                              # BLOCK capture, not a line allowlist (#608)
        if ("$line" -match '^\s*Failed\s+\S' -or "$line" -match '^\s*Error Message:') { $emit = $true }
        elseif ("$line" -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - inspect the full log above)" }
    Write-Output "LiteValidateSubsetTests / LiteValidationParityTests failing - fix scripts/lite/validate/ to match the harness (the C# is the spec for parity); never edit the tests."
    exit 1
}

# ZERO-MATCH GUARD (#455): key on the EXECUTED count (Passed+Failed), never on Total: or a message string.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped (pwsh missing?)."
    exit 1
}
exit 0
