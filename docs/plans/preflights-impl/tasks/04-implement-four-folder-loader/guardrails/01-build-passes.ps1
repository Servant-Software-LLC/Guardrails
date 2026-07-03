# catches: a loader/validator/model change that doesn't compile (or trips a warning under
#          TreatWarningsAsErrors=true).
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after the four-folder loader/validator change"
    exit 1
}
exit 0
