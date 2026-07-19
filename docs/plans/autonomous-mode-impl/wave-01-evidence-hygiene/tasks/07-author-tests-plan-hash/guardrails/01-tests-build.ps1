# catches: a plan-hash test file (or its command stub) that does NOT compile. With the minimal
#          PlanHashCommand stub + its CommandFactory registration the test project must build; a
#          non-compiling "test" exits dotnet test non-zero identically to a failing one, so without this
#          the TDD red is gameable (#155).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build — the plan-hash test file or its stub are not type-correct"
    exit 1
}
exit 0
