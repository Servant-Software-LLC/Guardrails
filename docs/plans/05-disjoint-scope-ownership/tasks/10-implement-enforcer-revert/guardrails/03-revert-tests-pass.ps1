# catches: a revert implementation whose behavior deviates from the authored tests (in-scope
#          change wrongly reverted, untracked-file #51 case not restored, --fresh not wiping the
#          baseline). Filtered to THIS milestone's tests.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~ScopeRevert" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "ScopeRevert tests are failing - M5 revert is not implemented to the tests' spec."
    exit 1
}
exit 0
