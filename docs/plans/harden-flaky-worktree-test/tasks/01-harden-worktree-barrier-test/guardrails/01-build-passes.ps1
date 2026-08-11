# catches: an edit that does not compile - garbage, or a real syntax/type error introduced
#          while hardening the test.
dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build after the hardening edit"
    exit 1
}
exit 0
