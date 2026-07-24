# catches: tautological attestation tests — tests that PASS against the NotImplementedException stubs
#          verify nothing. With the build green (guardrail 01), a non-zero exit here means the tests ran
#          and FAILED against the throwing stubs = TDD red. A zero exit means the behaviour is already
#          present (or the tests assert nothing) — either way tautological. (INVERSE check: non-zero is
#          success, so no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ReviewAttestationTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the ReviewAttestation tests PASS against the NotImplementedException stubs — they are tautological (no real attestation behaviour is asserted)"
    exit 1
}
exit 0
