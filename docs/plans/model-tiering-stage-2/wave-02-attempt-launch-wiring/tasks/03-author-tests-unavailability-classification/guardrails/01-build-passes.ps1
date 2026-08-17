# catches: a test file that does not COMPILE being accepted as the TDD "red" (#155). A non-compiling
#          test exits `dotnet test` non-zero identically to one that compiles and fails, so without
#          this the sibling 02-tests-fail-on-current-code would pass on garbage - and task 04 could
#          not fix it, because its writeScope excludes this test file.
# -v q is correct on a BUILD; it is FORBIDDEN on a `dotnet test` (dotnet.md 4/4.2).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "tests/Guardrails.Core.Tests does not compile - ConnectionUnavailabilityClassificationTests must COMPILE and FAIL, not fail to compile. ClaudeSignalClassifier is an INTERNAL type, but Guardrails.Core declares InternalsVisibleTo(Guardrails.Core.Tests), so calling it directly is legal - check the namespace/using. Read the compile errors above: they name the exact symbol."
    exit 1
}
exit 0
