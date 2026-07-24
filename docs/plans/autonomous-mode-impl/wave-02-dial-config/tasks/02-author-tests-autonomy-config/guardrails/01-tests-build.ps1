# catches: an autonomy-config test file (or its stubs) that does NOT compile. With the minimal
#          AutonomyConfig / RunConfig / RawRunConfig stubs the test project must build; a non-compiling
#          "test" exits dotnet test non-zero identically to a failing one, so without this the TDD red is
#          gameable (#155).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build — the autonomy-config test file or its stubs are not type-correct"
    exit 1
}
exit 0
