# catches: tautological tests - MergeLockAndSettleTests that pass against current code, which has no
#          net-new merge lock and no FF/non-FF settle refactor. The tests reference not-yet-existing
#          settle/lock surface, so the project will not compile against current code; a non-zero exit
#          (compile OR test failure) proves non-tautology. tests-build OMITTED (compile-coupled).
$file = "tests/Guardrails.Integration.Tests/MergeLockAndSettleTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~MergeLockAndSettleTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "MergeLockAndSettleTests PASS against current code - they are tautological (the FF/union settle + net-new merge lock do not exist yet)"
    exit 1
}
exit 0
