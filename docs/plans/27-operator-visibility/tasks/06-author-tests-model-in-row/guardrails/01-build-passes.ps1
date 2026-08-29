# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. A non-compiling
#          "test" exits dotnet test non-zero IDENTICALLY to a failing one, so without this the red
#          signal in 02-tests-fail-on-stubs is gameable by garbage (#155).
#
# WHY THE TEST PROJECT AND NOT src/Guardrails.Cli: tests/Guardrails.Integration.Tests references BOTH
# Guardrails.Core and Guardrails.Cli, so building it compiles the test file AND every production
# assembly it drives (LogSiteRenderer and LiveRunObserver both live in Guardrails.Cli). Building
# src/Guardrails.Cli alone would leave THIS TASK'S ONLY DELIVERABLE uncompiled and certify it green
# (the #176 transitive-compile-dependency trap, inverted).
#
# This task's red is MIXED, and this build check covers both halves. The log-site behaviours need no
# stub - LogSiteRenderer.ExportSite, WriteTaskPageIfHasAttempts, JournalDocument and AttemptProvenance
# are all already public, so those tests compile against the current tree and are red on OUTPUT (the
# model is absent from the HTML). The live-table behaviours are red on genuine stubs: the TWO members
# this task adds to LiveRunObserver.cs -
# `public static string ModelCell(string? runner, string? tier, bool climbed, bool substituted,
# bool isScript)` and `public static string ModelCellFromRoute(string runner, string? tier,
# string? requestedTier)` - each with a body that throws NotImplementedException. Building the test
# project compiles BOTH the stubs' assembly and the tests that drive them.
#
# It ALSO covers the one Group B pin that could not compile before 05-raise-attempt-route-resolved
# ran: the decorator-forwarding regression pin references IRunObserver.AttemptRouteResolved, a member
# that task added. That is the #176 transitive-compile-dependency rule satisfied by the chain ORDER -
# the contract task runs first precisely so this task's tests have something to bind to.
#
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto any dotnet test in this plan - there it would delete the failure
# detail the #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - ModelInRowTests.cs is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
