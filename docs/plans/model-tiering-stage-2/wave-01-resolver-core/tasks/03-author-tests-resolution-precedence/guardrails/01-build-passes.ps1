# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. The Resolve(...)
#          stub already exists (task 01 declared it), so these tests must build; a non-compiling
#          "test" exits dotnet test non-zero identically to a failing one, so without this the red
#          signal below is gameable by garbage (#155).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - TierResolverPrecedenceTests is not type-correct against the existing TierResolver.Resolve signature"
    exit 1
}
exit 0
