# catches: a DiagnosticCodes.cs edit or a LiteProfileContractTests file that does not compile (a
#          reservation written as a malformed constant, a test referencing a helper that does not exist).
#          Builds Core.Tests, which compiles Guardrails.Core with it.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - fix the compile errors above (DiagnosticCodes.cs or LiteProfileContractTests.cs)"
    exit 1
}
exit 0
