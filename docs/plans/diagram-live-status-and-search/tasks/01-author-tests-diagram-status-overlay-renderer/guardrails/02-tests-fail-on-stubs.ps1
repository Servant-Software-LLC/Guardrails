# catches: a test file that trivially passes (e.g. asserts nothing meaningful) instead
#          of genuinely failing against the throwing stub. Scoped via --filter to THIS
#          task's own new tests only (#193) - never the whole project, which would also
#          run pre-existing HtmlDiagramRendererTests that must stay green.
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj"

$output = & dotnet test $proj -c Release --nologo --filter "FullyQualifiedName~Render_With" 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -eq 0) {
    Write-Output "---"
    Write-Output "The new tests passed against the stub (or --filter FullyQualifiedName~Render_With matched zero tests) - they must genuinely FAIL until 02-implement-diagram-status-overlay-renderer lands the real behavior. Either the stub is not actually throwing/incomplete, the tests are not exercising the new behavior, or the filter does not match the new test names."
    exit 1
}

exit 0
