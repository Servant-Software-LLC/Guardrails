# catches: tautological terminal-phase tests - tests that PASS against current code (no <plan>/guardrails/
#          terminal phase) assert nothing about the terminal halt / revalidate / terminal-only resume. Build
#          is green (guardrail 01), so a non-zero exit here means the tests RAN and FAILED = TDD red. A zero
#          exit means they assert nothing new. INVERSE check - does NOT re-emit (#179).
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~PlanGuardrailPhase" --no-build --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the PlanGuardrailPhase tests PASS against current code (no terminal <plan>/guardrails/ phase) - they are tautological; they must assert the terminal halt (exit 2, durable branch), --revalidate-task plan:guardrails, and terminal-only resume"
    exit 1
}
exit 0
