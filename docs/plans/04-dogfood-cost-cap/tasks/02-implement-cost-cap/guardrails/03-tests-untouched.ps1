# catches: "making the tests pass" by editing the CostCap tests instead of the
#          implementation (the tests are the spec — the implementation task must not move them).
#          Uses content hashes stored by task 01 so this check is immune to git-commit timing
#          and to fresh-clone / detached-HEAD baselines.

$stateIn = $env:GUARDRAILS_STATE_IN
if (-not $stateIn -or -not (Test-Path $stateIn)) {
  Write-Output "GUARDRAILS_STATE_IN not set or missing — cannot verify test file integrity"
  exit 1
}

$state = Get-Content $stateIn -Raw | ConvertFrom-Json
$storedHashes = $state.'01-author-cost-cap-tests'.testFileHashes

if (-not $storedHashes) {
  Write-Output "State key '01-author-cost-cap-tests.testFileHashes' missing — task 01 may not have written its state fragment"
  exit 1
}

$files = @(
  "tests/Guardrails.Core.Tests/CostCapConfigTests.cs",
  "tests/Guardrails.Core.Tests/CostCapValidatorTests.cs",
  "tests/Guardrails.Core.Tests/CostCapSchedulerTests.cs"
)

$failures = @()
foreach ($file in $files) {
  $stored = $storedHashes.$file
  if (-not $stored) {
    $failures += "No stored hash for $file in task 01 state fragment"
    continue
  }
  if (-not (Test-Path $file)) {
    $failures += "$file was deleted by the implementation task"
    continue
  }
  # git hash-object computes the git blob SHA1 from on-disk content — content-based,
  # not index/commit-based, so a mid-execution git commit cannot game this check.
  $current = (git hash-object $file).Trim()
  if ($current -ne $stored.Trim()) {
    $failures += "$file was modified by the implementation task (expected $stored, got $current)"
  }
}

if ($failures) {
  Write-Output ($failures -join "; ")
  exit 1
}
exit 0
