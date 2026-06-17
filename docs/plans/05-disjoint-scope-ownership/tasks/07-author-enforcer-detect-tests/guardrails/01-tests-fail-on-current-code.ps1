# catches: tautological tests — enforcer tests that pass against current code (which has no
#          WorkspaceScopeEnforcer) would verify nothing. The tests reference the not-yet-existing
#          enforcer type, so the project won't compile against current code; a non-zero exit
#          (compile failure OR test failure) proves non-tautology.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~WorkspaceScopeEnforcer" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "The new WorkspaceScopeEnforcer tests PASS (or found nothing) against current code that has no enforcer - they are tautological. They must fail until M4 is implemented."
    exit 1
}
exit 0
