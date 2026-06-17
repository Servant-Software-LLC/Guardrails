# catches: a validation implementation whose diagnostics deviate from the authored tests
#          (wrong severity — GR2015 emitted as a warning not an error; a clean case wrongly
#          flagged; malformed glob not caught). Filtered to THIS milestone's tests.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~ScopeValidation" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "ScopeValidation tests are failing - GR2015/2016/2017 are not implemented to the tests' spec."
    exit 1
}
exit 0
