# catches: tautological criticality-assessment tests — tests that PASS against the stubbed decider
#          verify nothing. With the build green (guardrail 01), a non-zero exit here means the tests ran
#          and FAILED against the stub = TDD red. A zero exit means the assessment/threshold/clamp is
#          already implemented (or the tests assert nothing). (INVERSE check: non-zero is success, no
#          #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~CriticalityAssessmentTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the CriticalityAssessmentTests PASS against the stub — they are tautological (no real threshold/clamp/malformed-escalate behaviour is asserted)"
    exit 1
}
exit 0
