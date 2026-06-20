# catches: the 2ND-REVIEW BLOCKER - task 04 deliverable (b) "drop the exclusive admission gate from
#          Scheduler" is UNVERIFIED. Because script-action tasks already run concurrently under the
#          WorkspaceLock, a NO-OP edit that leaves Scheduler.cs's `_workspaceLock` field + its
#          Acquire/Release calls + the `exclusive` computation untouched passes every OTHER task-04
#          guardrail (the seam suite stays green, Core still builds, RestoreAncestorCaptures was removed
#          from the EXECUTOR not the scheduler). The exclusive admission gate would silently SURVIVE.
#          On current master Scheduler.cs holds: `private readonly WorkspaceLock _workspaceLock = new();`
#          then `await _workspaceLock.AcquireAsync(exclusive, ...)` / `_workspaceLock.Release(exclusive)`.
#          Dropping the exclusive admission gate MUST remove the `_workspaceLock` field and its uses, so
#          assert `_workspaceLock` is GONE from Scheduler.cs. (`_workspaceLock` is the precise symbol -
#          verified against the current Scheduler.cs at lines 27/156/165.)
#          Scoped to the one file this task owns (grep-scope rule).
$file = "src/Guardrails.Core/Execution/Scheduler.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - cannot verify the exclusive admission gate was dropped"
    exit 1
}
$text = Get-Content $file -Raw
if ($text -match '_workspaceLock\b') {
    Write-Output "Scheduler.cs still references _workspaceLock - the exclusive admission gate (WorkspaceLock Acquire/Release + the 'exclusive' computation) was NOT dropped. Task 04 deliverable (b) is incomplete: remove the _workspaceLock field and its AcquireAsync/Release uses so independent tasks are no longer serialized by the exclusive flag (worktree isolation replaces it)."
    exit 1
}
exit 0
