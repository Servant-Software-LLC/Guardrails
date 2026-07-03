# catches: a wiring test file that does not COMPILE - garbage, a real type error, or a warning
#          (TreatWarningsAsErrors=true). A non-compiling test exits `dotnet test` non-zero identically
#          to a failing one, so without this the TDD red signal (guardrail 02) is gameable by garbage (#155).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - the ReVerifierWiringTests file is not type-correct or has a warning (warnings are errors)"
    exit 1
}
exit 0
