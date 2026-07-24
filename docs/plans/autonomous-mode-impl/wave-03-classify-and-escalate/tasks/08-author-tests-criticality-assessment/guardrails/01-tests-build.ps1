# catches: a criticality-assessment test file (or its stub) that does NOT compile. With the
#          CriticalityJudge stub + decision result type the test project must build; a non-compiling
#          "test" exits dotnet test non-zero identically to a failing one, so without this the TDD red is
#          gameable (#155).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build — the criticality-assessment test file or its CriticalityJudge stub is not type-correct"
    exit 1
}
exit 0
