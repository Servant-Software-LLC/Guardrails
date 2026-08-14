# catches: tier tests that pass against throwing stubs - asserting nothing real, or the agent implemented
#          the behaviour instead of stubbing it.
$ErrorActionPreference = 'Stop'
& dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "Category=ModelTieringStage1" --nologo -v q 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Output "The tier tests PASS against the NotImplementedException stubs - they assert nothing real."
    exit 1
}
exit 0
