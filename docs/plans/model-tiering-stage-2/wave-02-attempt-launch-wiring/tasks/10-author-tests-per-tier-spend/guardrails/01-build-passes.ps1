# catches: a test file that does not COMPILE being accepted as the TDD "red" (#155). A non-compiling
#          test exits `dotnet test` non-zero identically to one that compiles and fails, so without
#          this the sibling 02-tests-fail-on-stubs would pass on garbage - and task 11 could not fix
#          it, because its writeScope excludes this test file.
# Builds the whole solution, not just the test project: this task adds a NEW production file
# (JournalTierSpend.cs) to Guardrails.Core, and a stub that breaks the Cli's compile would otherwise
# surface only at the wave union gate.
# -v q is correct on a BUILD; it is FORBIDDEN on a `dotnet test` (dotnet.md 4/4.2).
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "the solution does not compile - PerTierSpendTests must COMPILE and FAIL, not fail to compile, and the new JournalTierSpend stub must not break Guardrails.Core's consumers. Read the compile errors above: they name the exact symbol."
    exit 1
}
exit 0
