# catches: a test file that does not COMPILE - garbage, a wrong signature for
#          PlanPreflightPhase.EvaluateAsync, or a type this project cannot see. A non-compiling "test"
#          exits dotnet test non-zero identically to a genuinely failing one, so without this the RED
#          signal in 02-tests-fail-on-unwired-phase is gameable by garbage (#155) - and worse, task 05
#          could NOT repair it: SampleVerifierWiringTests.cs is outside task 05's write scope, so a
#          non-compiling test file delivered from here dead-ends the whole downstream chain at
#          needsHuman with no in-scope remedy (#193). This build check is the boundary that stops it.
#
#          tests/Guardrails.Integration.Tests is the right and smallest scope: it is the ONLY project
#          this task writes into, and it carries a ProjectReference to Guardrails.Cli (where
#          PlanPreflightPhase lives) as well as to Guardrails.Core. Building tests/Guardrails.Core.Tests
#          would compile NOTHING of this task's diff, since it references Guardrails.Core only and
#          cannot see PlanPreflightPhase at all.
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet test in 02 - there it would delete the failure detail the
# census and the #179 re-emit exist to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - SampleVerifierWiringTests.cs is not type-correct (see the compiler errors above). The tests are SUPPOSED to fail against the unwired phase; they are NOT supposed to fail to compile, and task 05 cannot fix this file for you."
    exit 1
}
exit 0
