# catches: a test that trivially passes instead of genuinely failing against the
# throwing stub. Scoped via --filter to THIS task's own new tests only (#193).
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj"

$output = & dotnet test $proj -c Release --nologo --filter "FullyQualifiedName~Search" 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -eq 0) {
    Write-Output "---"
    Write-Output "The new search-box tests passed against the stub (or --filter FullyQualifiedName~Search matched zero tests) - they must genuinely FAIL until 08-implement-diagram-search-box lands the real behavior."
    exit 1
}

exit 0
