# catches: an implementation that does not actually satisfy the authored tests.
# Scoped to this task's own tests only (#193) - the pre-existing HtmlDiagramRenderer
# tests are covered by the plan's baseline preflight and the terminal gate.
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj"

$output = & dotnet test $proj -c Release --nologo --filter "FullyQualifiedName~Render_With" 2>&1
$exitCode = $LASTEXITCODE
$fullLog = $output -join "`n"
Write-Output $fullLog

if ($exitCode -ne 0) {
    Write-Output "---"
    Write-Output "The status-overlay tests are still failing."
    $failureLines = $output | Select-String -Pattern "Error Message:|Assert\.|Exception|Expected:|Actual:|\[FAIL\]"
    foreach ($line in $failureLines) {
        Write-Output $line.Line
    }
    exit 1
}

exit 0
