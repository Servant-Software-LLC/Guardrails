# catches: a test that trivially passes instead of genuinely failing against the
# throwing stub. Scoped via --filter to the new test class only (#193).
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj"

$output = & dotnet test $proj -c Release --nologo --filter "FullyQualifiedName~OnTheFlyDiagramObserverTests" 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -eq 0) {
    Write-Output "---"
    Write-Output "The new tests passed against the stub (or --filter FullyQualifiedName~OnTheFlyDiagramObserverTests matched zero tests) - they must genuinely FAIL until 04-implement-diagram-observer lands the real behavior."
    exit 1
}

exit 0
