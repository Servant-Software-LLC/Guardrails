# catches: test code that doesn't compile (a "test" that can't run verifies nothing). These tests
#          exercise the EXISTING public PlanValidator API surface + diagnostic-code constants, so they
#          are expected to compile against current code - keep this build guardrail (the new behaviour
#          they assert does not require new symbols to compile).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build with the new ParallelValidationGateTests"
    exit 1
}
exit 0
