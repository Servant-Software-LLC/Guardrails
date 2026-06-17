# catches: code that doesn't compile (TreatWarningsAsErrors → a warning also fails the build)
dotnet build src/Guardrails.Core/Guardrails.Core.csproj --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "Guardrails.Core does not build after the GR2015/2016/2017 validation implementation."
    exit 1
}
exit 0
