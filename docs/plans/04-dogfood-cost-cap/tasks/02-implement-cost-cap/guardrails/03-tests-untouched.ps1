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
  # git hash-object computes the git blob SHA1 from the WORKING-TREE content — content-based,
  # not index- or commit-based — so a mid-execution `git add`/`git commit`/`git stash` cannot
  # game this check (the file's on-disk bytes are what is hashed, and what the build/test sees).
  # The baseline ($stored) was captured the same way by task 01 BEFORE this task ran, so the
  # comparison is against the pre-implementation content, never against HEAD or a fresh clone.
  $current = (git hash-object $file 2>$null).Trim()
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($current)) {
    $failures += "Could not hash $file (git hash-object failed) — cannot prove it is untouched"
    continue
  }
  if ($current -ne $stored.Trim()) {
    $failures += "$file was modified by the implementation task (expected $stored, got $current)"
  }
}

if ($failures) {
  Write-Output ($failures -join "; ")
  exit 1
}
exit 0
