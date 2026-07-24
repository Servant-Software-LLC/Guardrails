# catches: tautological escalation-sink tests — tests that PASS against the throwing FileEscalationSink
#          stub verify nothing. With the build green (guardrail 01), a non-zero exit here means the tests
#          ran and FAILED against the stub = TDD red. A zero exit means the sink is already implemented
#          (or the tests assert nothing). (INVERSE check: non-zero is success, no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~EscalationSinkTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the EscalationSinkTests PASS against the stub — they are tautological (no real record-to-disk / seq / decisions[] behaviour is asserted)"
    exit 1
}
exit 0
