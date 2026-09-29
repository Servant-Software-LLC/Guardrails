# catches: a test file that does not COMPILE - garbage, a wrong namespace, or a call to a LiteScriptHost
#          member that does not exist. Against the stub Graph.psm1 the integration test project must build;
#          a non-compiling "test" exits dotnet test non-zero exactly like a failing one, so without this
#          the red census below could be satisfied by garbage (#155).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - LiteValidateGraphTests.cs is not type-correct against LiteScriptHost (task 02) and Core's PlanLoader/PlanValidator"
    exit 1
}
exit 0
