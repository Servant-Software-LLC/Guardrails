# catches: dispatch tests that pass against throwing stubs - they assert nothing real, or the agent
#          implemented the dispatch instead of stubbing it. Build already green (01), so a non-zero test
#          exit here means the tests RAN and FAILED = TDD red.
$ErrorActionPreference = 'Stop'
& dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "Category=ModelTieringStage1" --nologo -v q 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Output "The dispatch tests PASS against the NotImplementedException stubs - they assert nothing real."
    exit 1
}
exit 0
