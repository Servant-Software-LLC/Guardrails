# catches: "making the tests pass" by editing the CostCap tests instead of the
#          implementation (the tests are the spec — the implementation task must not move them)
$changed = git diff --name-only HEAD -- tests/Guardrails.Core.Tests/CostCap*.cs
if ($changed) {
  Write-Output ("The implementation task modified authored test file(s): " + ($changed -join ", "))
  exit 1
}
exit 0
