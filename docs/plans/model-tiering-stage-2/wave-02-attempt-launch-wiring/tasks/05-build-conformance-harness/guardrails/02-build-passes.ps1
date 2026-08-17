# catches: a harness that does not COMPILE. It carries no tests of its own (TDD-exempt test
#          infrastructure), so a build failure here would otherwise surface inside task 06 - whose
#          writeScope EXCLUDES this file, leaving that task unable to fix what broke it and dead-ending
#          the whole conformance chain at needs-human.
# -v q is correct on a BUILD (it keeps the error lines and drops restore noise); it is FORBIDDEN on a
# `dotnet test` (dotnet.md 4/4.2).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "tests/Guardrails.Integration.Tests does not compile with the new Stage2PlanHarness. Read the compile errors above: they name the exact symbol. If the missing symbol belongs to a file outside this task's writeScope, write {\"needsHuman\": \"...\"} rather than editing it - note that AttemptProvenance's tier members and AttemptOutcome.NoRoute DO exist here (task 02 is an ancestor)."
    exit 1
}
exit 0
