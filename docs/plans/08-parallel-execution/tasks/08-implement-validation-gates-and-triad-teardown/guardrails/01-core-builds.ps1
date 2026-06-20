# catches: the gate / triad-teardown change left Guardrails.Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after the validation-gate / triad-teardown change"
    exit 1
}
exit 0
