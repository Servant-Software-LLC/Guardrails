# catches: tautological plan-hash tests — tests that PASS against the NotImplementedException stub verify
#          nothing. With the build green (guardrail 01), a non-zero exit here means the tests ran and
#          FAILED against the throwing handler = TDD red. A zero exit means plan-hash is already
#          implemented (or the tests assert nothing). (INVERSE check: non-zero is success, no #179 re-emit.)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~PlanHashCliTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the plan-hash tests PASS against the stub — they are tautological (no real hash-printing behaviour is asserted)"
    exit 1
}
exit 0
