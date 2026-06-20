# catches: removing the capture-seam call or the exclusive gate left Guardrails.Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after the de-serialize / begin-triad-teardown change"
    exit 1
}
exit 0
