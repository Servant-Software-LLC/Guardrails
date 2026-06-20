# catches: the triad teardown claimed-done but only PARTLY done. The plan (§Placement, §Milestones M2)
#          names the FULL teardown: delete WorkspaceLock, CapturedFileStore, FileHashCapture, the
#          RestoreAncestorCaptures executor seam, TaskNode.Exclusive/CaptureHashes/RestoreOnRetry, and
#          the two triad validators (ValidateCaptureHashPaths/ValidateCaptureHashes + ValidateRestoreOnRetry).
#          A teardown that deleted only WorkspaceLock + the TaskNode fields (the original check) would
#          pass while CapturedFileStore.cs / FileHashCapture.cs / the executor restore seam / the
#          validators lingered - dead triad code that contradicts "the triad is gone". Each negative is
#          deterministic: Test-Path FALSE for deleted files, file-scoped Select-String negatives for the
#          method/validator names in the one file each lives in (grep-scope rule - no project-tree greps).
$lock = "src/Guardrails.Core/Execution/WorkspaceLock.cs"
if (Test-Path $lock) {
    Write-Output "$lock still exists - WorkspaceLock was not deleted (triad teardown incomplete)"
    exit 1
}

# Deleted triad store files (plan §92 names both under Execution/).
foreach ($gone in @(
    "src/Guardrails.Core/Execution/CapturedFileStore.cs",
    "src/Guardrails.Core/Execution/FileHashCapture.cs")) {
    if (Test-Path $gone) {
        Write-Output "$gone still exists - the triad store class was not deleted (triad teardown incomplete)"
        exit 1
    }
}

$taskNode = "src/Guardrails.Core/Model/TaskNode.cs"
if (-not (Test-Path $taskNode)) {
    Write-Output "$taskNode does not exist"
    exit 1
}
$tn = Get-Content $taskNode -Raw
foreach ($field in @('Exclusive', 'CaptureHashes', 'RestoreOnRetry')) {
    if ($tn -match "(?m)public\s+[^\s]+\s+$field\s*\{\s*get") {
        Write-Output "TaskNode.cs still declares the triad property '$field' - it must be removed"
        exit 1
    }
}

# The RestoreAncestorCaptures executor seam (plan: TaskExecutor.cs:165/287) is a METHOD, not its own
# file - file-scoped negative grep in its containing file.
$executor = "src/Guardrails.Core/Execution/TaskExecutor.cs"
if (-not (Test-Path $executor)) {
    Write-Output "$executor does not exist"
    exit 1
}
if ((Get-Content $executor -Raw) -match 'RestoreAncestorCaptures') {
    Write-Output "$executor still references RestoreAncestorCaptures - the capture/restore seam was not removed from the executor (triad teardown incomplete)"
    exit 1
}

# The two triad validators must be gone from PlanValidator.cs (file-scoped negative). Accept either
# spelling of the capture-paths validator (master has ValidateCaptureHashPaths; the plan's M2 line item
# names it ValidateCaptureHashes) so the check survives either naming.
$planValidator = "src/Guardrails.Core/Loading/PlanValidator.cs"
if (-not (Test-Path $planValidator)) {
    Write-Output "$planValidator does not exist"
    exit 1
}
$pv = Get-Content $planValidator -Raw
foreach ($validator in @('ValidateCaptureHashPaths', 'ValidateCaptureHashes', 'ValidateRestoreOnRetry')) {
    if ($pv -match $validator) {
        Write-Output "$planValidator still defines/calls the triad validator '$validator' - the two triad validators must be deleted (triad teardown incomplete)"
        exit 1
    }
}

exit 0
