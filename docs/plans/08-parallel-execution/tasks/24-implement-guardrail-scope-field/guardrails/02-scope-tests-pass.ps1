# catches: the scope field / integration-set filter implemented wrong - GuardrailScopeTests still
#          failing (scope not parsed, or the B-3 colliding-sibling-unconditional vs distant-touched-files
#          split wrong)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~GuardrailScopeTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "GuardrailScopeTests failing - the guardrail scope field / integration-set filter is not implemented to spec"
    exit 1
}
exit 0
