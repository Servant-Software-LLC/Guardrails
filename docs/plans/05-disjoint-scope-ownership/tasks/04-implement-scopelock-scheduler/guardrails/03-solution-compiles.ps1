# catches: a removal that compiles in src/ but breaks the test projects - 01-build only builds
#          src/Guardrails.Core, so dropping Exclusive/WorkspaceLock can leave the test assemblies
#          uncompilable while every src-scoped check passes; it then surfaces downstream as a
#          mis-attributed tests-pass failure. Build the WHOLE solution (both test projects) so the
#          break is named AT task 04.
dotnet build Guardrails.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "Guardrails.sln does not compile after the ScopeLock/Exclusive removal - a consumer (tests, fixtures) still references the removed Exclusive/WorkspaceLock; update GoldenRoundTripTests.cs, PlanFixtures.cs, WorkspaceLockTests.cs."
    exit 1
}
exit 0
