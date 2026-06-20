# catches: the executor still drives the capture/restore seam - RestoreAncestorCaptures is still
#          invoked in TaskExecutor.cs, so the M1 "begin triad teardown" step was not actually done.
#          Scoped to the one file this task owns (grep-scope rule).
$file = "src/Guardrails.Core/Execution/TaskExecutor.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist"
    exit 1
}
if ((Get-Content $file -Raw) -match 'RestoreAncestorCaptures') {
    Write-Output "TaskExecutor.cs still references RestoreAncestorCaptures - the M1 capture/restore-seam removal was not done"
    exit 1
}
exit 0
