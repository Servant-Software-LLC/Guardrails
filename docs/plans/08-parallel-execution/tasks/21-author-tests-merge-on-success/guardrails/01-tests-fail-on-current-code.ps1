# catches: tautological tests - MergeOnSuccessTests that pass against current code, which has no
#          --merge-on-success end-of-run delivery. The tests reference the not-yet-existing flag /
#          hook, so the project will not compile against current code; a non-zero exit (compile OR test
#          failure) proves non-tautology. tests-build OMITTED (compile-coupled).
$file = "tests/Guardrails.Integration.Tests/MergeOnSuccessTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~MergeOnSuccessTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "MergeOnSuccessTests PASS against current code - they are tautological (--merge-on-success does not exist yet)"
    exit 1
}
exit 0
