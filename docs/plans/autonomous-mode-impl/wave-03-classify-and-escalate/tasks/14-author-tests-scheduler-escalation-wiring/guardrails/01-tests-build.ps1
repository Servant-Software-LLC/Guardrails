# catches: a wiring test file that does NOT compile. It references types produced by the ancestor impl
#          tasks (classifier / blocker / judge / sink / consumer); with those present the integration
#          test project must build. A non-compiling "test" exits dotnet test non-zero identically to a
#          failing one, so without this the tests-fail-on-current-code red is gameable (#155).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build — the SchedulerEscalationWiringTests file is not type-correct against the ancestor impl types"
    exit 1
}
exit 0
