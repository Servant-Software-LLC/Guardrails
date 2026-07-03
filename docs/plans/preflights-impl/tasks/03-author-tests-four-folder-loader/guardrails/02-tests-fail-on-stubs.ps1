# catches: tautological four-folder tests - tests that PASS against the current (stub) loader/validator
#          verify nothing. Build is green (guardrail 01), so a non-zero exit here unambiguously means the
#          tests RAN and FAILED = TDD red: the loader doesn't populate the four folders and the validator
#          doesn't emit GR2027+/re-home GR2018/retire GR2017 yet. A zero exit means the tests assert
#          nothing new. INVERSE check - does NOT re-emit (#179).
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~FourFolder" --no-build --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the FourFolder loader/validation tests PASS against the current stub loader/validator - they are tautological; they must assert the four folders are loaded and the new GR2027+/re-homed-GR2018/GR2017-retirement diagnostics, which do not exist yet"
    exit 1
}
exit 0
