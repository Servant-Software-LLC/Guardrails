# catches: a wiring change that does not COMPILE. This task edits ONE file -
#          src/Guardrails.Cli/PlanPreflightPhase.cs - but building the Cli alone is NOT the right scope:
#          the ancestor task 04 wrote SampleVerifierWiringTests.cs into
#          tests/Guardrails.Integration.Tests, that file is present in this task's segment, and
#          guardrails 03 and 04 both run it. Building the Cli alone would leave it UNCOMPILED, so a
#          signature change here that breaks the delivered test would sail past this check and surface
#          only when 03 tries to run it (the #176 transitive-compile-dependency trap). The Integration
#          test project carries a ProjectReference to Guardrails.Cli AND to Guardrails.Core, so building
#          it compiles both this task's edit and everything that depends on it. Building
#          tests/Guardrails.Core.Tests would compile NEITHER, since it references Guardrails.Core only
#          and cannot see PlanPreflightPhase at all.
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet test in 03 - there it would delete the failure detail the
# #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - the sample-verification step you added to PlanPreflightPhase.cs is not type-correct, or it changed EvaluateAsync's signature and broke the callers (RunCommand.cs, Revalidate.cs, PlanPreflightPhaseTests, SampleVerifierWiringTests). Those files are OUTSIDE your write scope: fix PlanPreflightPhase.cs to compile against them, do not edit them (see the compiler errors above)."
    exit 1
}
exit 0
