# catches: a task or a merge that leaves the full test suite red even though every
#          per-task guardrail passed individually (e.g. an interaction between the
#          wave-1 renderer change and the wave-2 search-box change not caught by
#          either task's own scoped tests).
$ws = $env:GUARDRAILS_WORKSPACE
$sln = Join-Path $ws "Guardrails.sln"

$coreOutput = & dotnet test (Join-Path $ws "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj") -c Release --nologo 2>&1
$coreExit = $LASTEXITCODE
$integrationOutput = & dotnet test (Join-Path $ws "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj") -c Release --nologo 2>&1
$integrationExit = $LASTEXITCODE

Write-Output ($coreOutput -join "`n")
Write-Output ($integrationOutput -join "`n")

if ($coreExit -ne 0 -or $integrationExit -ne 0) {
    Write-Output "---"
    Write-Output "Full test suite is red on the merged plan-branch HEAD."
    $failureLines = ($coreOutput + $integrationOutput) | Select-String -Pattern "Error Message:|Assert\.|Exception|Expected:|Actual:|\[FAIL\]"
    foreach ($line in $failureLines) {
        Write-Output $line.Line
    }
    exit 1
}

exit 0
