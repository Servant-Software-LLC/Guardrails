# catches: a gate-classifier test file (or its stub) that does NOT compile. With the GateClassifier +
#          GateClass stub the test project must build; a non-compiling "test" exits dotnet test non-zero
#          identically to a failing one, so without this the TDD red is gameable (#155).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build — the gate-classifier test file or its GateClassifier/GateClass stub is not type-correct"
    exit 1
}
exit 0
