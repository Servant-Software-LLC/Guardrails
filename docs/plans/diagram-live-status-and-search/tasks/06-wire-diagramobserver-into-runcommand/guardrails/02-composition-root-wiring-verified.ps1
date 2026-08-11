# catches: OnTheFlyDiagramObserver existing and being independently well-tested (tasks
# 03/04) while nothing in RunCommand.cs actually constructs and injects it - the #120
# false-green where a component is fully built but dead from the CLI. This drives the
# REAL composition-root test authored in 05-author-tests-diagramobserver-wiring, which
# runs the real CLI end-to-end and asserts diagram.html reflects live status - it does
# NOT inject the observer itself, so it can only pass once this task's wiring is real.
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj"

$output = & dotnet test $proj -c Release --nologo --filter "FullyQualifiedName~DiagramLiveStatusWiringTests" 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -ne 0) {
    Write-Output "---"
    Write-Output "OnTheFlyDiagramObserver is not actually wired into RunCommand.cs's composition root - diagram.html does not reflect live status from a real run."
    $failureLines = $output | Select-String -Pattern "Error Message:|Assert\.|Exception|Expected:|Actual:|\[FAIL\]"
    foreach ($line in $failureLines) {
        Write-Output $line.Line
    }
    exit 1
}

exit 0
