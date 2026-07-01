# catches: a tautological wiring test - one that PASSES against the CURRENT (serial-null) factory
#          verifies nothing about the unconditional wiring. Build is green (guardrail 01), so a non-zero
#          exit here unambiguously means the wiring test RAN and FAILED = TDD red: the factory does not
#          yet wire IReVerifier at MaxParallelism 1. A zero exit means the test does not actually assert
#          the serial-mode wiring (tautological). This is the INVERSE check - it does NOT re-emit (#179).
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ReVerifierWiring" --no-build --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the ReVerifierWiring tests PASS against the current serial-null factory - they do not assert the unconditional wiring (tautological); the serial-mode _reVerifier non-null assertion must be present and must fail on current code"
    exit 1
}
exit 0
