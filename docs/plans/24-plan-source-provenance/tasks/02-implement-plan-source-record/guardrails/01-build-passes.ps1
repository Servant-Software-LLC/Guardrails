# catches: an implementation that does not COMPILE - a stub reshaped in a way the existing test file
#          no longer type-checks against, or a plain syntax error. It runs FIRST so a compile failure
#          reports as a compile failure, rather than reaching 02 where a non-zero dotnet test exit is
#          indistinguishable from a genuinely failing assertion (#155).
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet test in 02 - there it would delete the failure detail the
# #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - PlanSourceRecord.cs no longer type-checks against PlanSourceRecordTests.cs (the test file is out of this task's write scope: implement to it, do not reshape around it)"
    exit 1
}
exit 0
