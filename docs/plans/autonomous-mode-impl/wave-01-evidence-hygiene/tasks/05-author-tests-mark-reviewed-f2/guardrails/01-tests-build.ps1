# catches: an F2 test file (or its command stubs) that does NOT compile — garbage, or a real syntax/type
#          error. With the minimal MarkReviewedCommand option stubs the test project must build; a
#          non-compiling "test" exits dotnet test non-zero identically to a failing one, so without this
#          the TDD red is gameable (#155).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build — the mark-reviewed F2 test file or its stubs are not type-correct"
    exit 1
}
exit 0
