# catches: a ScopeLock/Scheduler implementation whose concurrency or fairness behavior
#          deviates from the authored tests (independent narrow scopes not actually running
#          concurrently, universal scopes not serializing, FIFO skip-ahead). Filtered to THIS
#          milestone's tests, not the whole suite.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~ScopeLock|FullyQualifiedName~ScopeScheduler" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "ScopeLock/ScopeScheduler tests are failing - M2 is not implemented to the tests' spec."
    exit 1
}
exit 0
