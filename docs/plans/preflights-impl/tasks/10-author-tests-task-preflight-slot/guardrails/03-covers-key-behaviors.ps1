# catches: a task-preflight test file that satisfies build + tests-fail-on-current-code with one trivial
#          test while skipping the no-burn assertion, cone isolation, or serial+worktree coverage.
#          Lower-bound presence grep, scoped to the one file this task owns.
$f = Get-Content "tests/Guardrails.Integration.Tests/TaskPreflightSlotTests.cs" -Raw
if ($f -notmatch 'task-preflight-failed|TaskPreflight') {
    Write-Output "TaskPreflightSlotTests.cs does not reference the task-preflight-failed outcome"
    exit 1
}
if ($f -notmatch '(?i)attempt') {
    Write-Output "TaskPreflightSlotTests.cs does not assert the attempt count (the no-burn property, #8) - a preflight failure must NOT burn a retry"
    exit 1
}
if ($f -notmatch '(?i)blocked|independent|cone|isolation') {
    Write-Output "TaskPreflightSlotTests.cs does not cover cone-blocking / independent-branch isolation (#8)"
    exit 1
}
if ($f -notmatch 'MaxParallelism|Parallelism|worktree|Worktree') {
    Write-Output "TaskPreflightSlotTests.cs does not appear to exercise BOTH serial and worktree mode (the no-burn property is structural, not budget-dependent)"
    exit 1
}
exit 0
