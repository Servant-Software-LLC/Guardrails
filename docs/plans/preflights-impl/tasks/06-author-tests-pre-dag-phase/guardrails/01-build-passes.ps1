# catches: the pre-DAG phase test file doesn't COMPILE (garbage, type error, or a warning under
#          TreatWarningsAsErrors=true) - which exits `dotnet test` non-zero identically to a genuine red,
#          making guardrail 02 gameable by garbage (#155).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - PlanPreflightPhaseTests is not type-correct (or a warning tripped warnings-as-errors)"
    exit 1
}
exit 0
