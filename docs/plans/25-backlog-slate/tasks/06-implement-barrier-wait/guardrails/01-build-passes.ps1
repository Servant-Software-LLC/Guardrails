# catches: a wiring change that does not COMPILE - a BarrierWait surface the existing test file no
#          longer type-checks against, or a Scheduler edit with a plain syntax/type error. It runs
#          FIRST so a compile failure reports as a compile failure, rather than reaching 03 where a
#          non-zero dotnet test exit is indistinguishable from a genuinely failing assertion (#155).
#
# Scope: the SOLUTION, not tests/Guardrails.Core.Tests - and that is the point (#176). This task edits
# Scheduler.cs, which lives in Guardrails.Core and is CONSUMED by Guardrails.Cli (SchedulerFactory,
# RunCommand). tests/Guardrails.Core.Tests references Guardrails.Core ONLY, so building it would leave
# every Cli call site UNCOMPILED: a Scheduler surface change could leave this guardrail green while the
# CLI does not build. The solution build is the smallest scope that actually covers this task's diff.
# (Task 05, whose diff is entirely inside the Core+Core.Tests closure, deliberately builds only the
# test project - the scopes differ because the diffs differ, not by oversight.)
#
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet test in 03 - there it would delete the failure detail the
# #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build - BarrierWait.cs or the barrier wiring in Scheduler.cs is not type-correct (see the compiler errors above). BarrierWaitTests.cs is OUT of this task's write scope: implement to it, do not reshape around it."
    exit 1
}
exit 0
