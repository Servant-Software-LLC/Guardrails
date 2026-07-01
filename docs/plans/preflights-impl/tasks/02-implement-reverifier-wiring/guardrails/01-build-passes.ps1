# catches: a SchedulerFactory change that doesn't compile (or trips a warning under
#          TreatWarningsAsErrors=true).
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build after the SchedulerFactory wiring change"
    exit 1
}
exit 0
