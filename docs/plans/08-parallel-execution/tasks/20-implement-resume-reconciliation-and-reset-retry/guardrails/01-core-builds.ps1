# catches: the resume / reset-retry change left Guardrails.Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after the resume reconciliation / reset-retry change"
    exit 1
}
exit 0
