# catches: tautological autonomy-config tests — tests that PASS against the stubbed parse verify
#          nothing. With the build green (guardrail 01), a non-zero exit here means the tests ran and
#          FAILED against the NotImplementedException mapping = TDD red. A zero exit means the parse is
#          already implemented (or the tests assert nothing). (INVERSE check: non-zero is success, no
#          #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AutonomyConfigTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the AutonomyConfig tests PASS against the stubs — they are tautological (no real autonomy-block parse is asserted)"
    exit 1
}
exit 0
