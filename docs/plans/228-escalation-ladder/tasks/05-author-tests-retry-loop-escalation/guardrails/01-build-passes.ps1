# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. This task writes
#          no stub (TaskExecutor already exists, so the file compiles against today's tree and is red at
#          RUNTIME), which makes this check MORE load-bearing, not less: a non-compiling "test" exits
#          dotnet test non-zero identically to a failing one, so without this the red signal below is
#          gameable by garbage (#155).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - RetryLoopEscalationTests.cs is not type-correct"
    exit 1
}
exit 0
