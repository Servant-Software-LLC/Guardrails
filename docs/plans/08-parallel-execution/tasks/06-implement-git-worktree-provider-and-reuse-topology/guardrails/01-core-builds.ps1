# catches: code that doesn't compile - GitWorktreeProvider / WorktreeManager left Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after adding GitWorktreeProvider"
    exit 1
}
exit 0
