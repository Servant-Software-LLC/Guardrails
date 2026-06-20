# catches: the write-scope check implemented wrong - WriteScopeCheckTests still failing (out-of-scope
#          edit not caught, rename D+A not handled, deletion path not checked, TDD test-exclusion not
#          enforced, or the absent-scope off-switch broken)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~WriteScopeCheckTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "WriteScopeCheckTests failing - the write-scope check / scoped revert is not implemented to spec"
    exit 1
}
exit 0
