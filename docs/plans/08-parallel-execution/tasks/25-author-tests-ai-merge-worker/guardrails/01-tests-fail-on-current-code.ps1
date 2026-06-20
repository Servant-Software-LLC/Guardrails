# catches: tautological tests - AiMergeWorkerTests that pass against current code, which has no
#          AiMergeResolver and no GUARDRAILS_MERGE_* env contract. The tests reference the
#          not-yet-existing worker, so the project will not compile against current code; a non-zero exit
#          (compile OR test failure) proves non-tautology. tests-build OMITTED (compile-coupled).
$file = "tests/Guardrails.Integration.Tests/AiMergeWorkerTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~AiMergeWorkerTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "AiMergeWorkerTests PASS against current code - they are tautological (the AI-merge worker / merge env contract do not exist yet)"
    exit 1
}
exit 0
