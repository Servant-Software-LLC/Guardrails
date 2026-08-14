# catches: a test file that PASSES against the throwing stubs - i.e. asserts nothing real (a tautology),
#          or the agent implemented the behaviour instead of stubbing it. With the build already green
#          (01), a non-zero test exit here unambiguously means the tests RAN and FAILED = TDD red.
$ErrorActionPreference = 'Stop'
& dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "Category=ModelTieringStage1" --nologo -v q 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Output "The authored tests PASS against the NotImplementedException stubs - they assert nothing real. Encode the plan's behaviours so they fail before the implementation lands."
    exit 1
}
exit 0
