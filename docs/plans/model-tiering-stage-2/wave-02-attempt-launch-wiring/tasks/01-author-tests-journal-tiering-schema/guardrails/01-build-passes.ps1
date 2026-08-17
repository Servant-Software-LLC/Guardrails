# catches: a test file that does not COMPILE being accepted as the TDD "red" (#155). A non-compiling
#          test exits `dotnet test` non-zero identically to one that compiles and fails, so without
#          this the sibling 02-tests-fail-on-stubs would pass on garbage - and the implementation
#          task could not fix it, because its writeScope excludes this test file. Garbage fails HERE,
#          unambiguously.
# -v q is correct on a BUILD (it keeps the error lines and drops the restore noise); it is FORBIDDEN
# on a `dotnet test` (it suppresses the Error Message/Expected/Actual block, dotnet.md 4/4.2).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "tests/Guardrails.Core.Tests does not compile - the authored JournalTieringSchemaTests must COMPILE and FAIL, not fail to compile. Read the compile errors above: they name the exact symbol. If the missing symbol is in a file outside this task's writeScope, write {\"needsHuman\": \"...\"} rather than editing it."
    exit 1
}
exit 0
