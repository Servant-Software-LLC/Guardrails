# catches: an implementation that does not actually satisfy the authored tests.
$ErrorActionPreference = 'Stop'
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "Category=ModelTieringStage1" --nologo -v q 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }
if ($code -ne 0) {
    Write-Output ""
    Write-Output "--- failure detail ---"
    $log | Select-String -Pattern '^\s*(Failed|Error Message|Assert\.|Expected|Actual)' | ForEach-Object { Write-Output $_.Line }
    Write-Output "The Stage 1 schema tests still fail - fix the implementation, not the tests."
    exit 1
}
exit 0
