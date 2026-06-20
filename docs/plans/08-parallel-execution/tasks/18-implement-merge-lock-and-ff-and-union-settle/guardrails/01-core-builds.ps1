# catches: the settle / lock refactor left Guardrails.Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after the merge-lock / FF-union-settle refactor"
    exit 1
}
exit 0
