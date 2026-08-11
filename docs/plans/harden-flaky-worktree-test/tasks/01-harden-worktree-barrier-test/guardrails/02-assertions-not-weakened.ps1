# catches: a "fix" that makes the test pass by WEAKENING what it proves instead of fixing the
#          actual race - e.g. changing `Assert.Equal(3, executor.AssignedWorktreePaths.Count)` to
#          `Assert.True(executor.AssignedWorktreePaths.Count >= 1)`. The action prompt explicitly
#          forbids this in prose, but nothing structural enforced it - and a weakened assertion is
#          PERVERSE here: it makes guardrail 04's 20x-repeat loop EASIER to pass, not harder, since
#          it now tolerates the exact 2-of-3 race issue #214 exists to catch (found by an
#          adversarial guardrails-review pass, not guessed). This check locks in the four load-
#          bearing assertions verbatim so a "fix" cannot quietly loosen them and still go green.
$ErrorActionPreference = 'Stop'
$path = "tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs"
$content = Get-Content -Raw -Path $path

# Isolate the barrier-test method body: it is the LAST method in the file, so grab from its
# signature to end-of-file. (If a later edit adds methods after it, this still finds the method -
# it just also captures trailing content, which the required-assertion checks below tolerate.)
$marker = 'Scheduler_DrivesThreeIndependentTasks_WithWorktreeHandles_OverlapProvenByBarrier'
$idx = $content.IndexOf($marker)
if ($idx -lt 0) {
    Write-Output "could not find $marker in $path - has the test been renamed or removed? re-add it or update this guardrail"
    exit 1
}
$body = $content.Substring($idx)

$required = @(
    @{ Name = 'AllSucceeded';            Pattern = 'Assert\.True\(\s*report\.AllSucceeded' },
    @{ Name = 'AssignedWorktreePaths==3'; Pattern = 'Assert\.Equal\(\s*3\s*,\s*executor\.AssignedWorktreePaths\.Count\s*\)' },
    @{ Name = 'Distinct==3';              Pattern = 'Assert\.Equal\(\s*3\s*,\s*executor\.AssignedWorktreePaths\.Distinct\(\)\.Count\(\)\s*\)' },
    @{ Name = 'CreatedSegments==3';       Pattern = 'Assert\.Equal\(\s*3\s*,\s*provider\.CreatedSegments\.Count\s*\)' },
    @{ Name = 'IntegrateCallCount==3';    Pattern = 'Assert\.Equal\(\s*3\s*,\s*provider\.IntegrateCallCount\s*\)' }
)

$missing = @()
foreach ($check in $required) {
    if ($body -notmatch $check.Pattern) {
        $missing += $check.Name
    }
}

if ($missing.Count -gt 0) {
    Write-Output "the barrier test's load-bearing assertion(s) were weakened or removed: $($missing -join ', ')"
    Write-Output "this test must still assert the FULL 3-way concurrency proof verbatim - do not tolerate fewer than 3 arrivals, do not use Assert.True(... >= N) in place of Assert.Equal(3, ...)"
    exit 1
}

exit 0
