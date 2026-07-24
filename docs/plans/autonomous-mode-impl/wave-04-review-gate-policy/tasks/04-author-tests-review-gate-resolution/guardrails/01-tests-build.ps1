# catches: a SchedulerReviewGateTests file that does NOT compile. It references only SHIPPED symbols
#          (ReviewGateDecision / DecisionTokens.ProceededUnreviewed / IEscalationSink / ReviewMarker /
#          the wave-loop harness), so it must build against current code; a non-compiling "test" exits
#          dotnet test non-zero identically to a red one, masking a genuine compile error the wiring task
#          (whose writeScope excludes this file) could never fix (#155).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build — the SchedulerReviewGateTests file is not type-correct against the shipped symbols"
    exit 1
}
exit 0
