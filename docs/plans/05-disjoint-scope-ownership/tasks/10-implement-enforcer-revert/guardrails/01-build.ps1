# catches: code that doesn't compile (TreatWarningsAsErrors → a warning also fails the build)
dotnet build src/Guardrails.Core/Guardrails.Core.csproj --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "Guardrails.Core does not build after the M5 revert implementation."
    exit 1
}
exit 0
