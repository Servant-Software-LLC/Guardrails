# catches: a rendering change that does not COMPILE, and a change that compiles in Guardrails.Cli but
#          breaks the test assembly that drives it. Building src/Guardrails.Cli alone would leave
#          ModelInRowTests.cs uncompiled, so guardrail 02's `--no-build` would then run against a
#          STALE test binary and certify the old bytes green - the #176 transitive-compile-dependency
#          trap. tests/Guardrails.Integration.Tests references BOTH Guardrails.Core and Guardrails.Cli,
#          so building it is the smallest scope that actually covers this task's diff plus its proof.
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet test in 02/04 - there it would delete the failure detail
# the #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - the LiveRunObserver / LogSiteRenderer / ConsoleRunObserver change is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
