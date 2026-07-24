# catches: tautological blocker-retry tests — tests that PASS against the stubbed loop verify nothing.
#          With the build green (guardrail 01), a non-zero exit here means the tests ran and FAILED
#          against the stub = TDD red. A zero exit means the bounded-wait loop is already implemented (or
#          the tests assert nothing). (INVERSE check: non-zero is success, no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~BlockerRetryTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the BlockerRetryTests PASS against the stub — they are tautological (no real bounded wait/backoff/ceiling behaviour is asserted)"
    exit 1
}
exit 0
