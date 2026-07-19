# catches: an autonomous-CLI test file (or the new run-command option stubs) that does NOT compile. With
#          the --autonomous/--dial option stubs the test project must build; a non-compiling "test" exits
#          dotnet test non-zero identically to a failing one, so without this the TDD red is gameable (#155).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build — the autonomous-CLI test file or its option stubs are not type-correct"
    exit 1
}
exit 0
