# catches: tautological tests — revert tests that pass against current code (M4 detect-only, no
#          RevertOutOfScope, no scope-baseline wipe) would verify nothing. The tests reference the
#          not-yet-existing revert method, so the project won't compile against current code; a
#          non-zero exit (compile failure OR test failure) proves non-tautology.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~ScopeRevert" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "The new ScopeRevert tests PASS (or found nothing) against current code that has only M4 detect-only - they are tautological. They must fail until M5 revert is implemented."
    exit 1
}
exit 0
