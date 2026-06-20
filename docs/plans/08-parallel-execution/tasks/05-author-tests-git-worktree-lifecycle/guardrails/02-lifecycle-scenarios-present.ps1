# catches: a vacuous GitWorktreeLifecycleTests that compiles-fails (passing tests-fail-on-current-code
#          trivially on, say, only the GitWorktreeProvider reference) while never encoding the
#          load-bearing §1/M2 lifecycle scenarios - the plan-branch creation, linear-chain worktree
#          REUSE, the W-2 fork-off-RECORDED-sha gate, and discard/prune. Assert each concern is present.
#          Scoped to the one file this task owns (grep-scope rule - no project-tree greps).
$file = "tests/Guardrails.Integration.Tests/GitWorktreeLifecycleTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the worktree-lifecycle test file was not authored"
    exit 1
}
$text = Get-Content $file -Raw
$needles = @{
    'guardrails/'                        = 'no "guardrails/" token - the plan-branch (guardrails/<plan-name>) creation scenario is missing'
    '(?i)reuse|linear|same.{0,12}worktree' = 'no reuse/linear-chain term - the "ONE segment worktree reused along a linear chain" scenario (the reuse lever) is missing'
    '(?i)recorded|RecordedSha'           = 'no recorded-sha term - the W-2 fork-the-rest-off-the-producer-RECORDED-sha gate is missing'
    '(?i)discard|prune'                  = 'no discard/prune term - the freed-worktree teardown scenario is missing'
}
foreach ($n in $needles.Keys) {
    if ($text -notmatch $n) {
        Write-Output "GitWorktreeLifecycleTests: $($needles[$n])"
        exit 1
    }
}
exit 0
