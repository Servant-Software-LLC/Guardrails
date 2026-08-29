# catches: a wiring change that does not COMPILE. This task edits code in TWO assemblies -
#          Guardrails.Cli (PlanPreflightPhase) and Guardrails.Integration.Tests (the wiring test) - and
#          tests/Guardrails.Integration.Tests is the smallest scope that covers BOTH: it carries a
#          ProjectReference to Guardrails.Cli AND to Guardrails.Core, so building it compiles this
#          task's whole diff. Building the Cli alone would leave the new test file UNCOMPILED and the
#          guardrail would certify a broken test project green (the #176 transitive-compile-dependency
#          trap); building tests/Guardrails.Core.Tests would compile NEITHER edit, since it references
#          Guardrails.Core only and cannot see PlanPreflightPhase at all.
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet test in 03 - there it would delete the failure detail the
# #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - the sample-verification step in PlanPreflightPhase.cs or SampleVerifierWiringTests.cs is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
