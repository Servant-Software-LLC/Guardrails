# catches: tautological pre-DAG phase tests - tests that PASS against current code (which has no pre-DAG
#          phase) assert nothing about the halt/marker/resume behavior. Build is green (guardrail 01), so a
#          non-zero exit here means the tests RAN and FAILED = TDD red (the phase is genuinely absent). A
#          zero exit means they assert nothing new. INVERSE check - does NOT re-emit (#179).
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~PlanPreflightPhase" --no-build --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the PlanPreflightPhase tests PASS against current code (which has no pre-DAG phase) - they are tautological; they must assert the halt (exit 2, zero attempts), the planPreflights marker, and the B1 resume SKIP"
    exit 1
}
exit 0
