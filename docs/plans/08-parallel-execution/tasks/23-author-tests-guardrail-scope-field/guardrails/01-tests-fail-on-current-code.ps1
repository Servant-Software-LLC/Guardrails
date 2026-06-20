# catches: tautological tests - GuardrailScopeTests that pass against current code, which has no
#          GuardrailDefinition.Scope and no integration-set filter. The tests reference the
#          not-yet-existing field/filter, so the project will not compile against current code; a
#          non-zero exit (compile OR test failure) proves non-tautology. tests-build OMITTED.
$file = "tests/Guardrails.Core.Tests/GuardrailScopeTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~GuardrailScopeTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "GuardrailScopeTests PASS against current code - they are tautological (the guardrail scope field / integration-set filter do not exist yet)"
    exit 1
}
exit 0
