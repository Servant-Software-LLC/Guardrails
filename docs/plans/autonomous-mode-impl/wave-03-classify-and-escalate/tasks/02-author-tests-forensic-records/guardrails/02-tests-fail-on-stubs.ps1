# catches: tautological forensic-records tests — tests that PASS against the throwing AutonomyJsonl
#          stub verify nothing. With the build green (guardrail 01), a non-zero exit here means the tests
#          ran and FAILED against the NotImplementedException writer = TDD red. A zero exit means the
#          jsonl writer is already implemented (or the tests assert nothing). (INVERSE check: non-zero is
#          success, no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AutonomyForensicRecordsTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the AutonomyForensicRecordsTests PASS against the stubs — the autonomy.jsonl writer red is tautological (no real append behaviour is asserted)"
    exit 1
}
exit 0
