# catches: a wiring change that does not COMPILE. This task edits code in THREE assemblies -
#          Guardrails.Core (InitialBreakdownInvoker), Guardrails.Cli (BreakdownCommand) and
#          Guardrails.Core.Tests (the wiring test) - and no single project covers all three:
#          tests/Guardrails.Core.Tests references Guardrails.Core ONLY, so building it would leave the
#          BreakdownCommand edit UNCOMPILED and the guardrail would certify a broken CLI green (the
#          #176 transitive-compile-dependency trap). The solution build is the smallest scope that
#          actually covers this task's diff.
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet test in 04 - there it would delete the failure detail the
# #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build - the recorder/gate wiring in InitialBreakdownInvoker.cs, BreakdownCommand.cs or PlanSourceWiringTests.cs is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
