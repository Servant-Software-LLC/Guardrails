# catches: a change that breaks the test project's build (lock.ps1 is not compiled, so a break here means
#          something outside scope was touched, or an upstream merge left the project uncompilable).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - see the compile errors above"
    exit 1
}
exit 0
