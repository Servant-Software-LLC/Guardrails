# catches: a wiring change that does not compile.
$ws = $env:GUARDRAILS_WORKSPACE
$sln = Join-Path $ws "Guardrails.sln"

$output = & dotnet build $sln -c Release --nologo 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -ne 0) {
    Write-Output "---"
    Write-Output "The solution does not compile after wiring OnTheFlyDiagramObserver into RunCommand.cs."
    exit 1
}

exit 0
