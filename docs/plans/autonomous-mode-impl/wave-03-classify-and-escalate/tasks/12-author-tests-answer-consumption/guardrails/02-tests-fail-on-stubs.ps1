# catches: tautological answer-consumption tests — tests that PASS against the throwing consumer stub
#          verify nothing. With the build green (guardrail 01), a non-zero exit here means the tests ran
#          and FAILED against the stub = TDD red. A zero exit means consumption is already implemented (or
#          the tests assert nothing). (INVERSE check: non-zero is success, no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AnswerFileConsumptionTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the AnswerFileConsumptionTests PASS against the stub — they are tautological (no real binding/CAS/injection/rejection behaviour is asserted)"
    exit 1
}
exit 0
