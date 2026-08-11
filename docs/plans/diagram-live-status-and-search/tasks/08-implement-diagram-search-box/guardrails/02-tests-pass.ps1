# catches: an implementation that does not actually satisfy the authored tests.
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj"

$output = & dotnet test $proj -c Release --nologo --filter "FullyQualifiedName~Search" 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -ne 0) {
    Write-Output "---"
    Write-Output "The search-box tests are still failing."
    $failureLines = $output | Select-String -Pattern "Error Message:|Assert\.|Exception|Expected:|Actual:|\[FAIL\]"
    foreach ($line in $failureLines) {
        Write-Output $line.Line
    }
    exit 1
}

exit 0
