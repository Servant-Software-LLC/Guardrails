# catches: tautological RunOutcomePolicy tests — tests that PASS against the stubbed
#          SuppressesDelivery/ProceededUnreviewedWaveCount verify nothing. With the build green (guardrail
#          01), a non-zero exit here means the tests ran and FAILED against the stub = TDD red. A zero exit
#          means the policy is already implemented (or the tests assert nothing). (INVERSE check: non-zero
#          is success, no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~RunOutcomePolicyTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the RunOutcomePolicyTests PASS against the stub — they are tautological (no real machine-decision-to-outcome mapping is asserted)"
    exit 1
}
exit 0
