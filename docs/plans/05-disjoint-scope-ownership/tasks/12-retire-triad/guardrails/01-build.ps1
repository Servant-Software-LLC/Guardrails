# catches: the triad removal breaking compilation (a dangling reference to a removed member,
#          a loader that no longer parses). TreatWarningsAsErrors → a warning also fails.
dotnet build src/Guardrails.Core/Guardrails.Core.csproj --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "Guardrails.Core does not build after retiring the test-protection triad."
    exit 1
}
exit 0
