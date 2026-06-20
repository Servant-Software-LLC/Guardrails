# catches: the NeedsHumanTriage step left Guardrails.Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after adding the NeedsHumanTriage step"
    exit 1
}
exit 0
