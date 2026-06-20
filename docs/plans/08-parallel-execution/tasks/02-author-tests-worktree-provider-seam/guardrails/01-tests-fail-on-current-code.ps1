# catches: tautological tests - WorktreeProviderSeamTests that pass (or vacuously do nothing)
#          against the current code, which has no IWorktreeProvider / channel envelope. The new
#          tests reference not-yet-existing symbols, so the project will not compile against current
#          code; a non-zero exit (compile failure OR test failure) is the proof of non-tautology.
#          (tests-build is intentionally OMITTED: a build guardrail would fail at the same instant
#          for the same missing symbols - it would add noise without signal, per the catalogue's
#          compile-coupled-tests rule.)
$file = "tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~WorktreeProviderSeamTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "WorktreeProviderSeamTests PASS against current code - they are tautological (the seam does not exist yet, so they must fail or fail-to-compile)"
    exit 1
}
exit 0
