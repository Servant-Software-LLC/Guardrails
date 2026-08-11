# catches: a test file that does not compile.
$ws = $env:GUARDRAILS_WORKSPACE
$proj = Join-Path $ws "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj"

$output = & dotnet build $proj -c Release --nologo 2>&1
$exitCode = $LASTEXITCODE
Write-Output ($output -join "`n")

if ($exitCode -ne 0) {
    Write-Output "---"
    Write-Output "Guardrails.Core.Tests does not compile - the new search-box test and/or stub has a build error."
    exit 1
}

exit 0
