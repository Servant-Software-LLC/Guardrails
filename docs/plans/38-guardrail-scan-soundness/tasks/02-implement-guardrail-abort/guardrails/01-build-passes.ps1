# catches: an implementation that does not compile - the shim's embedded-resource wiring and the
#          InterpreterMap/GuardrailRunner edits touch three files, and a build error in any of them
#          would otherwise surface as an ambiguous test failure in guardrail 02.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # a non-zero dotnet exit is DATA here, not an error

$out = dotnet build src/Guardrails.Core/Guardrails.Core.csproj --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }
if ($buildExit -ne 0) {
    Write-Output "src/Guardrails.Core does not compile (see the compiler errors above)"
    exit 1
}
exit 0
