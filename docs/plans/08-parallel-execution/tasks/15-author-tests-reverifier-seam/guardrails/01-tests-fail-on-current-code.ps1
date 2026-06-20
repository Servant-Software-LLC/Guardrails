# catches: tautological tests - ReVerifierSeamTests that pass against current code, which has only the
#          internal sealed attempt-bound GuardrailRunner and no public attempt-decoupled IReVerifier.
#          The tests reference not-yet-existing symbols, so the project will not compile against current
#          code; a non-zero exit (compile OR test failure) proves non-tautology. tests-build OMITTED
#          (compile-coupled).
$file = "tests/Guardrails.Core.Tests/ReVerifierSeamTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~ReVerifierSeamTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "ReVerifierSeamTests PASS against current code - they are tautological (IReVerifier does not exist yet)"
    exit 1
}
exit 0
