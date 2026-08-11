# catches: a test that trivially passes today (e.g. asserts nothing meaningful about
# live status) instead of genuinely failing because OnTheFlyDiagramObserver is not yet
# wired into RunCommand.cs. This test references no new type, so a compile failure is
# not the red signal here - only a genuine runtime assertion failure is.
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj"

$output = & dotnet test $proj -c Release --nologo --filter "FullyQualifiedName~DiagramLiveStatusWiringTests" 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -eq 0) {
    Write-Output "---"
    Write-Output "The wiring test passed against unwired code (or --filter FullyQualifiedName~DiagramLiveStatusWiringTests matched zero tests) - it must genuinely FAIL until 06-wire-diagramobserver-into-runcommand actually wires OnTheFlyDiagramObserver into RunCommand.cs. Either the test is not actually asserting on live status, something already (accidentally) wires it, or the filter does not match the new test name."
    exit 1
}

exit 0
