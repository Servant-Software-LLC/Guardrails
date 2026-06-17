# catches: tautological tests — ScopeLock/Scheduler tests that pass against current code
#          (no ScopeLock type, no scope-aware Scheduler) would verify nothing. The tests
#          reference not-yet-existing symbols, so the project won't compile against current
#          code; a non-zero exit (compile failure OR test failure) proves non-tautology. No
#          separate tests-build guardrail (it would fail at the same moment for the same reason).
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~ScopeLock|FullyQualifiedName~ScopeScheduler" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "The new ScopeLock/ScopeScheduler tests PASS (or found nothing) against current code that has no ScopeLock - they are tautological. They must fail until M2 is implemented."
    exit 1
}
exit 0
