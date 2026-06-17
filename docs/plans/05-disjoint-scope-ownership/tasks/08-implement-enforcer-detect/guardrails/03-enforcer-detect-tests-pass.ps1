# catches: a detect implementation whose behavior deviates from the authored tests (out-of-scope
#          write not detected, in-scope edit wrongly flagged, no-op touch flagged, enforcementIgnore
#          not honored). Filtered to THIS milestone's tests.
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~WorkspaceScopeEnforcer" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "WorkspaceScopeEnforcer tests are failing - M4 detect-only is not implemented to the tests' spec."
    exit 1
}
exit 0
