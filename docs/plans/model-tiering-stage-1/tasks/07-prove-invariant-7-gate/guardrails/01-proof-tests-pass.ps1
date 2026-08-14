# catches: a gate that leaks - an unconfigured breakdown emitting a tier artefact, which would silently
#          change every single-model user's output.
$ErrorActionPreference = 'Stop'
$log = & dotnet test tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj --filter "Category=ModelTieringStage1" --nologo -v q 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }
if ($code -ne 0) {
    Write-Output ""
    Write-Output "--- failure detail ---"
    $log | Select-String -Pattern '^\s*(Failed|Error Message|Assert\.|Expected|Actual)' | ForEach-Object { Write-Output $_.Line }
    Write-Output "Invariant 7 is not holding - an unconfigured breakdown is emitting tier artefacts."
    exit 1
}
exit 0
