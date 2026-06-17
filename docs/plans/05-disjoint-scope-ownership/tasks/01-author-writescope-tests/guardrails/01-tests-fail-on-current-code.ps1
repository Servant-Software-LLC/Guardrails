# catches: tautological tests — WriteScope tests that compile and PASS against current
#          code (which has no WriteScope type) would verify nothing. Because the tests
#          reference the not-yet-existing WriteScope type, the project will not even
#          compile against current code; a non-zero exit (compile failure OR test
#          failure) is the proof the tests are non-tautological. No separate tests-build
#          guardrail is used here (it would fail at the same moment for the same reason).
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~WriteScope" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "The new WriteScope tests PASS (or vacuously found nothing) against current code that has no WriteScope type - they are tautological. They must fail until WriteScope is implemented."
    exit 1
}
exit 0
