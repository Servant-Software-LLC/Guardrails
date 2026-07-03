# catches: the terminal phase change doesn't compile across Core + Cli (or trips a warning under
#          TreatWarningsAsErrors=true).
dotnet build src/Guardrails.Cli --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Cli (and its Core reference) does not build after the terminal phase change"
    exit 1
}
exit 0
