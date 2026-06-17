# catches: TWO fakes the build cannot see (types compile independently), on the files this
#          task owns (grep-scope rule, stacks/dotnet.md §2/§5):
#   (a) leaving WorkspaceLock in place + a dead ScopeLock.cs nothing consumes - assert the
#       CONSUMER (Scheduler.cs) references ScopeLock and no longer constructs WorkspaceLock;
#   (b) skipping the §4.5 clean removal of `exclusive` - assert TaskNode dropped Exclusive
#       and gained a WriteScope-typed field.
$lock = "src/Guardrails.Core/Execution/ScopeLock.cs"
if (-not (Test-Path $lock)) {
    Write-Output "$lock does not exist - ScopeLock was not created."
    exit 1
}
$sched = "src/Guardrails.Core/Execution/Scheduler.cs"
$schedCode = Get-Content $sched -Raw
if ($schedCode -notmatch '\bScopeLock\b') {
    Write-Output "$sched does not reference ScopeLock - the Scheduler was not rewired to the new lock (ScopeLock is dead code)."
    exit 1
}
if ($schedCode -match 'new\s+WorkspaceLock\b') {
    Write-Output "$sched still constructs a WorkspaceLock - the exclusive lock was not generalized to ScopeLock as M2 requires."
    exit 1
}
$model = "src/Guardrails.Core/Model/TaskNode.cs"
$modelCode = Get-Content $model -Raw
if ($modelCode -match '(?m)\bbool\?\s+Exclusive\b') {
    Write-Output "$model still declares an 'Exclusive' property - M2 §4.5 requires the exclusive field be removed (re-expressed as writeScope universal)."
    exit 1
}
if ($modelCode -notmatch '\bWriteScope\b') {
    Write-Output "$model does not declare a WriteScope-typed field - the writeScope field was not added to the task model."
    exit 1
}
exit 0
