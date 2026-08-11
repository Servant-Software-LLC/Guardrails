# catches: tautological tests that already pass against the current advice - they would certify the
#          correction without it being made.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~RetryPolicySalvageAdviceTests" 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Output "the salvage-advice tests PASS against the current advice - they assert nothing; encode the corrected advice so they fail first"
    exit 1
}
exit 0
