# catches: tautological F2 tests — tests that PASS against the NotImplementedException stub verify
#          nothing. With the build green (guardrail 01), a non-zero exit here means the tests ran and
#          FAILED against the stubbed F2 path = TDD red. A zero exit means F2 is already present (or the
#          tests assert nothing). (INVERSE check: non-zero is success, so no #179 re-emit.)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~MarkReviewedF2Tests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the mark-reviewed F2 tests PASS against the stubs — they are tautological (no real F2 behaviour is asserted)"
    exit 1
}
exit 0
