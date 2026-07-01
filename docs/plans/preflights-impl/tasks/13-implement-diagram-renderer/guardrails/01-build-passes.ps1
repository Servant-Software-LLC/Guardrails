# catches: the renderer rewrite doesn't compile across Core + Cli (or trips a warning under
#          TreatWarningsAsErrors=true).
dotnet build src/Guardrails.Cli --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Cli (and its Core reference) does not build after the renderer rewrite"
    exit 1
}
exit 0
