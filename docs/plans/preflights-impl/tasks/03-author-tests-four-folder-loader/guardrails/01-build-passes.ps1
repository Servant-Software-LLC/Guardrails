# catches: the test files or the model/diagnostic stubs don't COMPILE (garbage, a type error, or a
#          warning under TreatWarningsAsErrors=true). With the stubs present the test project must build;
#          a non-compiling test exits `dotnet test` non-zero identically to a failing one, so without this
#          the TDD red (guardrail 02) is gameable by garbage (#155).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - the four-folder tests or the model/DiagnosticCodes stubs are not type-correct (or a warning tripped warnings-as-errors)"
    exit 1
}
exit 0
