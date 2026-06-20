# catches: the guardrail scope field / filter change left Guardrails.Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after adding the guardrail scope field"
    exit 1
}
exit 0
