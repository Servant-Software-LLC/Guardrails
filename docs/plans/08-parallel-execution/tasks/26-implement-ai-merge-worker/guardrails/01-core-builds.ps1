# catches: the AI-merge worker / merge-env-contract change left Guardrails.Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after adding the AI-merge worker"
    exit 1
}
exit 0
