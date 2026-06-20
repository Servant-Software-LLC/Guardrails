# catches: the IReVerifier seam left Guardrails.Core non-building
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after adding the IReVerifier seam"
    exit 1
}
exit 0
