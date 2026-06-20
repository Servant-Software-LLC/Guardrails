# catches: tautological tests - GitWorktreeLifecycleTests that pass against current code, which has
#          no GitWorktreeProvider. The tests reference not-yet-existing symbols, so the project will
#          not compile against current code; a non-zero exit (compile OR test failure) proves
#          non-tautology. tests-build is OMITTED (compile-coupled: it would fail for the same missing
#          symbols at the same instant - noise without signal).
$file = "tests/Guardrails.Integration.Tests/GitWorktreeLifecycleTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~GitWorktreeLifecycleTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "GitWorktreeLifecycleTests PASS against current code - they are tautological (GitWorktreeProvider does not exist yet)"
    exit 1
}
exit 0
