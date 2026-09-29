# catches: a LiteScriptHost / LiteScriptHostTests pair that does not compile (a mis-typed API, a missing
#          using) - which would also break every downstream Lite test class compiled into this project.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - fix the compile errors above in Lite/LiteScriptHost*.cs"
    exit 1
}
exit 0
