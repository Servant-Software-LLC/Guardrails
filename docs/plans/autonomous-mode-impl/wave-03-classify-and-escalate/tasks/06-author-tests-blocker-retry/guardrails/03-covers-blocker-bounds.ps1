# catches: a BlockerRetryTests file that tests only resolve-within-ceiling and drops the ceiling-escalate bounds (§4.2). LOWER BOUND.
$test = "tests/Guardrails.Core.Tests/BlockerRetryTests.cs"
if (-not (Test-Path $test)) { Write-Output "$test does not exist"; exit 1 }
$c = Get-Content -Raw -Path $test
$missing = @()
if ($c -notmatch '(?i)MaxAttempts')      { $missing += 'MaxAttempts-ceiling' }
if ($c -notmatch '(?i)TotalWaitSeconds') { $missing += 'TotalWaitSeconds-ceiling' }
if ($c -notmatch '(?i)escalat')          { $missing += 'ceiling-escalates' }
if ($missing.Count -gt 0) { Write-Output ("BlockerRetryTests missing: " + ($missing -join ', ')); exit 1 }
exit 0
