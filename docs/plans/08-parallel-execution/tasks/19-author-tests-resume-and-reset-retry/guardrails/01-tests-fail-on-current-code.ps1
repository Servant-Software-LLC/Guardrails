# catches: tautological tests - ResumeAndResetRetryTests that pass against current code, which has the
#          journal-only resume and no FF-trailer / W-1 / taskBase reset-retry. The tests reference the
#          not-yet-existing reconciliation surface, so the project will not compile against current code;
#          a non-zero exit (compile OR test failure) proves non-tautology. tests-build OMITTED.
$file = "tests/Guardrails.Integration.Tests/ResumeAndResetRetryTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ResumeAndResetRetryTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "ResumeAndResetRetryTests PASS against current code - they are tautological (FF-trailer resume / W-1 / taskBase reset-retry do not exist yet)"
    exit 1
}
exit 0
