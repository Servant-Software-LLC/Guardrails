# catches: the agent claimed it wrote the cost-cap tests but produced no test files
$files = @(
  "tests/Guardrails.Core.Tests/CostCapConfigTests.cs",
  "tests/Guardrails.Core.Tests/CostCapValidatorTests.cs",
  "tests/Guardrails.Core.Tests/CostCapSchedulerTests.cs"
)
$missing = $files | Where-Object { -not (Test-Path $_) }
if ($missing) {
  Write-Output ("Missing cost-cap test file(s): " + ($missing -join ", "))
  exit 1
}
exit 0
