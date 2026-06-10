# catches: tautological cost-cap tests — tests that already pass against code with NO
#          maxCostUsd support encode nothing about the new behavior. Before implementation
#          the tests must FAIL (they will not even compile, since MaxCostUsd /
#          CostCapNonPositive do not exist yet — a non-zero exit covers both cases).
dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter "FullyQualifiedName~CostCap" --nologo
if ($LASTEXITCODE -eq 0) {
  Write-Output "The CostCap tests PASS (or build clean) against current code - they are tautological; they must fail before the feature exists."
  exit 1
}
exit 0
