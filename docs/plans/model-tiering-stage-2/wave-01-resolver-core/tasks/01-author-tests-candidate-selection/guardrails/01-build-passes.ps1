# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. With the minimal
#          TierResolver/TierResolution stubs this task writes, the Core test project must build; a
#          non-compiling "test" exits dotnet test non-zero identically to a failing one, so without
#          this the red signal below is gameable by garbage (#155).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - the test file or the TierResolver/TierResolution stubs are not type-correct"
    exit 1
}
exit 0
