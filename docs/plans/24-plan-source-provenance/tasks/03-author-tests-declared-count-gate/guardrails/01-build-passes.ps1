# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. With the
#          minimal stubs this task wrote, the test project must build; a non-compiling "test"
#          exits dotnet test non-zero identically to a failing one, so without this the red
#          signal in 02-tests-fail-on-stubs is gameable by garbage (#155).
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto any dotnet test in this plan - there it would delete the failure
# detail the #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - DeclaredCountGateTests.cs or the DeclaredCountGate stub is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
