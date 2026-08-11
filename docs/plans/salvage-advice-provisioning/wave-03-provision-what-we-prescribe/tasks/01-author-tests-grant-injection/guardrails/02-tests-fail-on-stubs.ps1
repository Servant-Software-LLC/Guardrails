# catches: tautological tests that already pass against the throwing stub - they would certify the
#          provisioning without it being built. The build guardrail proves they COMPILE, so a non-zero
#          exit here unambiguously means they RAN and FAILED against the stub (the TDD red).
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ToolGrantInjectionTests" 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Output "the grant-injection tests PASS against the stub - they assert nothing; encode the missing injection so they fail first"
    exit 1
}
exit 0
