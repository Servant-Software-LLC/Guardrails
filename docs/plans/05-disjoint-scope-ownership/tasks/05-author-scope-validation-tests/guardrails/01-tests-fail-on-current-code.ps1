# catches: tautological tests — scope-validation tests that pass against current code (which
#          has no GR2015/2016/2017 logic) would verify nothing. The tests reference not-yet-existing
#          diagnostic-code constants and validator behavior, so the project won't compile against
#          current code; a non-zero exit (compile failure OR test failure) proves non-tautology.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~ScopeValidation" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "The new ScopeValidation tests PASS (or found nothing) against current code that has no GR2015/2016/2017 - they are tautological. They must fail until M3 is implemented."
    exit 1
}
exit 0
