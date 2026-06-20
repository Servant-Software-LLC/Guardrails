# catches: tautological tests - WriteScopeCheckTests that pass against current code, which has no
#          WriteScopeCheck. The tests reference not-yet-existing symbols, so the project will not
#          compile against current code; a non-zero exit (compile OR test failure) proves non-tautology.
#          tests-build is OMITTED (compile-coupled).
$file = "tests/Guardrails.Integration.Tests/WriteScopeCheckTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~WriteScopeCheckTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "WriteScopeCheckTests PASS against current code - they are tautological (WriteScopeCheck does not exist yet)"
    exit 1
}
exit 0
