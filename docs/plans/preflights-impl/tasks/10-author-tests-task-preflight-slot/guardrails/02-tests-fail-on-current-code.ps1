# catches: tautological task-preflight tests - tests that PASS against current code (no task-preflight slot)
#          assert nothing about the no-burn / cone-blocking behavior. Build is green (guardrail 01), so a
#          non-zero exit here means the tests RAN and FAILED = TDD red. INVERSE check - does NOT re-emit (#179).
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~TaskPreflightSlot" --no-build --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the TaskPreflightSlot tests PASS against current code (no task-preflight slot) - they are tautological; they must assert task-preflight-failed / needs-human with NO attempt burned and cone isolation"
    exit 1
}
exit 0
