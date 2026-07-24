# catches: tautological auto-tier tests — tests that PASS against the stub (Decide unchanged) verify
#          nothing about the new gate. With the build green (guardrail 01), a non-zero exit here means the
#          tests ran and FAILED against the stub = TDD red (the auto+block-present silent-auto-apply case
#          must fail, because the stub still prompts). A zero exit means the auto-tier is already
#          implemented or the tests assert nothing. (INVERSE check: non-zero is success, no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~OverwatchAutoTierTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the OverwatchAutoTierTests PASS against the stub — they are tautological (the silent-auto-apply-under-autonomy-block case is not asserted, or Decide was changed here instead of in the implement task)"
    exit 1
}
exit 0
