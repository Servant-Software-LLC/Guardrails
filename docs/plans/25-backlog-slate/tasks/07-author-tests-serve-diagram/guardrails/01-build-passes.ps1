# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. A non-compiling
#          "test" exits dotnet test non-zero IDENTICALLY to a failing one, so without this the red
#          signal in 02-tests-fail-on-stubs is gameable by garbage (#155).
#
# WHY THE TEST PROJECT AND NOT src/Guardrails.Cli: tests/Guardrails.Integration.Tests references BOTH
# Guardrails.Core and Guardrails.Cli, so building it compiles the test file AND every production
# assembly it drives. Building src/Guardrails.Cli alone would leave THIS TASK'S ONLY DELIVERABLE
# uncompiled and certify it green (the #176 transitive-compile-dependency trap, inverted).
#
# This task writes NO stub (see its action prompt): every API ServeDiagramTests drives -
# LogServer.TryStart / BaseUrl / TaskNode - is already public today, so the tests compile against the
# current tree and are red on BEHAVIOUR, not on a NotImplementedException.
#
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto any dotnet test in this plan - there it would delete the failure
# detail the #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - ServeDiagramTests.cs is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
