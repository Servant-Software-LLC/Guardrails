# catches: a vacuous WorktreeProviderSeamTests that compiles-fails (passing tests-fail-on-current-code
#          trivially on, say, only the IWorktreeProvider reference) while never encoding the load-bearing
#          seam scenarios - the WorktreeHandle carried by the per-task channel ENVELOPE and the
#          3-tasks-with-OVERLAPPING-windows drive. Assert each concern is present. Scoped to the one
#          file this task owns (grep-scope rule - no project-tree greps).
$file = "tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the seam test file was not authored"
    exit 1
}
$text = Get-Content $file -Raw
$needles = @{
    'WorktreeHandle'            = 'WorktreeHandle is not referenced - the handle the channel envelope carries is not exercised'
    'IWorktreeProvider'         = 'IWorktreeProvider is not referenced - the seam under test is missing'
    '(?i)overlap|barrier|gate'  = 'no overlap/barrier/gate term - the "3 tasks with overlapping execution windows" scenario (asserted via a gate, not wall-clock) is missing'
    '(?i)channel|envelope'      = 'no channel/envelope term - the per-task channel envelope (TaskNode + assigned WorktreeHandle) scenario is missing'
}
foreach ($n in $needles.Keys) {
    if ($text -notmatch $n) {
        Write-Output "WorktreeProviderSeamTests: $($needles[$n])"
        exit 1
    }
}
exit 0
