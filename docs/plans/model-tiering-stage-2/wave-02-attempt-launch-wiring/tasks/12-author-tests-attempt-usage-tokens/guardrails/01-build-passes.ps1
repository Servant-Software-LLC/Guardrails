# catches: a test file that does not COMPILE being accepted as the TDD "red" (#155). A non-compiling
#          test exits `dotnet test` non-zero identically to one that compiles and fails, so without
#          this the sibling 02-tests-fail-on-stubs would pass on garbage - and task 13 could not fix
#          it, because its writeScope excludes this test file.
# Builds the whole solution, not just the test project: this task adds members to TWO production
# types (ClaudeResult in Guardrails.Core, PromptResult consumed by Guardrails.Cli), and a stub that
# breaks a consumer's compile would otherwise surface only at the wave union gate.
# -v q is correct on a BUILD; it is FORBIDDEN on a `dotnet test` (dotnet.md 4/4.2, #462).
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "the solution does not compile - AttemptUsageTokensTests must COMPILE and FAIL, not fail to compile, and the new ClaudeUsage/PromptUsage stub members must not break their consumers. Read the compile errors above: they name the exact symbol. If the missing symbol is AttemptUsage or AttemptRecord.Usage, that is task 01/02's deliverable in JournalModel.cs and is OUTSIDE your write scope - emit a needsHuman fragment rather than declaring it yourself."
    exit 1
}
exit 0
