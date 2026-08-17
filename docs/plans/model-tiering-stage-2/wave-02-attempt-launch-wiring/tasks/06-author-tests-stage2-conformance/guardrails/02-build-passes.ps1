# catches: a conformance suite that does not COMPILE being accepted as the TDD "red" (#155). A
#          non-compiling suite exits `dotnet test` non-zero identically to one that compiles and
#          fails, so without this the sibling 03-tests-fail-on-current-code would pass on garbage -
#          and tasks 07/08/09 could not fix it, because their writeScopes all exclude this test file.
#          Garbage fails HERE, unambiguously.
# -v q is correct on a BUILD; it is FORBIDDEN on a `dotnet test` (dotnet.md 4/4.2).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "tests/Guardrails.Integration.Tests does not compile - Stage2ConformanceTests must COMPILE and FAIL, not fail to compile. Every member the clauses need already exists on this tree: AttemptProvenance's Runner/Kind/Tier/TierSource/Effort and AttemptOutcome.NoRoute came from task 02, Stage2PlanHarness from task 05 - both ancestors. Read the compile errors above: they name the exact symbol. If the missing symbol is in a file outside this task's writeScope, write {\"needsHuman\": \"...\"} rather than editing it."
    exit 1
}
exit 0
