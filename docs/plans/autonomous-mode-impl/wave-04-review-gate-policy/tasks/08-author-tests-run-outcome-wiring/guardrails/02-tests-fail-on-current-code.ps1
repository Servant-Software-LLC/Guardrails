# catches: tautological run-outcome-wiring tests — a RunOutcomeWiringTests that PASSES against current
#          code verifies nothing, because the finalize flip (task 09), the review-gate resolution (task 05),
#          and the distinct exit code (task 10) are NOT wired yet. The test compiles (guardrail 01, shipped
#          symbols + literals) but must FAIL now = TDD red. A zero exit means the tests assert nothing real.
#          (INVERSE check: non-zero is success, no #179 re-emit.)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~RunOutcomeWiringTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the RunOutcomeWiringTests PASS against current (unwired) code — they are tautological; they must fail until tasks 05/09/10 wire the review-gate resolution + finalize flip + distinct exit code"
    exit 1
}
exit 0
