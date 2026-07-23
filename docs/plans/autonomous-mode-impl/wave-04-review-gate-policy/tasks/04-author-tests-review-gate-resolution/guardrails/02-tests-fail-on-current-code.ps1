# catches: tautological review-gate tests — a SchedulerReviewGateTests that PASSES against current code
#          verifies nothing, because the review-gate resolution is NOT wired yet (task 05 wires it). The
#          test compiles (guardrail 01, shipped symbols only) but must FAIL now = TDD red. A zero exit
#          means the tests assert nothing real (or the behavior already exists — it does not). (INVERSE
#          check: non-zero is success, no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~SchedulerReviewGateTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the SchedulerReviewGateTests PASS against current (unwired) code — they are tautological; they must fail until task 05 wires the review-gate resolution"
    exit 1
}
exit 0
