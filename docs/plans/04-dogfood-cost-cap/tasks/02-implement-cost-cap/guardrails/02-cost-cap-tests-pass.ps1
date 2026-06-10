# catches: a cost-cap implementation whose behavior deviates from the authored tests
#          (field not parsed, GR2010 missing, scheduler does not halt at the cap)
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~CostCap" --nologo
if ($LASTEXITCODE -ne 0) {
  Write-Output "CostCap tests are failing - the cost-cap feature is not implemented to the tests' spec."
  exit 1
}
exit 0
