# catches: tautological validation tests — tests that PASS while the validator emits neither GR2039 nor
#          GR2040 verify nothing. With the build green (guardrail 01), a non-zero exit here means the
#          tests ran and FAILED because the validation is not yet implemented = TDD red. A zero exit means
#          the matrix is already satisfied (or the tests assert nothing). (INVERSE check: non-zero is
#          success, no #179 re-emit.)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~AutonomyValidatorTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the AutonomyValidator tests PASS with no validation implemented — they are tautological (the GR2039/GR2040 matrix asserts nothing)"
    exit 1
}
exit 0
