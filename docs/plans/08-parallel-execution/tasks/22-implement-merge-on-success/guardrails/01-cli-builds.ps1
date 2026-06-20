# catches: the --merge-on-success flag wiring left the CLI project non-building (Cli references Core,
#          so this also covers the Core hook change)
dotnet build src/Guardrails.Cli --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Cli does not build after adding --merge-on-success"
    exit 1
}
exit 0
