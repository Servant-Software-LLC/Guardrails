# catches: garbage tests - a LiteLockTests file that does not COMPILE. A non-compiling test file exits
#          dotnet test non-zero exactly like a red one, so without this gate it would read as TDD red (#155).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - LiteLockTests must compile against LiteScriptHost and Core"
    exit 1
}
exit 0
