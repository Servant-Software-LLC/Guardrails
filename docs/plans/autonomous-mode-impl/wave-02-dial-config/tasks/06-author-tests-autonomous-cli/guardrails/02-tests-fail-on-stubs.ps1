# catches: tautological autonomous-CLI tests — tests that PASS while --autonomous/--dial resolve nothing
#          verify nothing. With the build green (guardrail 01), a non-zero exit here means the tests ran
#          and FAILED against the unimplemented resolution = TDD red. A zero exit means the flags already
#          resolve (or the tests assert nothing). (INVERSE check: non-zero is success, no #179 re-emit.)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~AutonomousModeCliTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the AutonomousModeCli tests PASS with no resolution implemented — they are tautological (--autonomous/--dial assert nothing)"
    exit 1
}
exit 0
