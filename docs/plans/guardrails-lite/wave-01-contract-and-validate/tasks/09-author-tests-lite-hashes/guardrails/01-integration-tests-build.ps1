# catches: garbage tests - LiteHashParityTests / LiteMarkReviewedTests that do not COMPILE. A test file
#          that does not build exits dotnet test non-zero exactly like a red one, so without this gate a
#          non-compiling file would read as TDD red (#155).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - the authored Lite hash tests must compile against LiteScriptHost and Core"
    exit 1
}
exit 0
