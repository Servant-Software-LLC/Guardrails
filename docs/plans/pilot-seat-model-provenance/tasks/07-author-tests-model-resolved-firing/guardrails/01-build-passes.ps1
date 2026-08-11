# catches: a test file or its stubs that do not COMPILE (#155) - a non-compiling test exits dotnet
#          test non-zero identically to a failing one, so without this the red signal is gameable.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
Set-Location $ws
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output 'tests/Guardrails.Integration.Tests does not build - the tests or stubs are not type-correct.'
    exit 1
}
exit 0
