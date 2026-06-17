# catches: a detect-only enforcer that exists and unit-tests green but is never invoked by the
#          real attempt path - the build passes and the unit tests construct the enforcer directly,
#          so without this the seam in TaskExecutor could be missing and enforcement would never run
#          in production. Assert the enforcer file exists AND TaskExecutor references it (cross-module
#          wiring, stacks/dotnet.md §2). Scoped to the two files this task owns (grep-scope rule).
$enforcer = "src/Guardrails.Core/Execution/WorkspaceScopeEnforcer.cs"
if (-not (Test-Path $enforcer)) {
    Write-Output "$enforcer does not exist - the enforcer collaborator was not created."
    exit 1
}
if ((Get-Content $enforcer -Raw) -notmatch '(?m)\bSnapshot\b') {
    Write-Output "$enforcer does not declare a Snapshot member - the pre-action snapshot is missing."
    exit 1
}
$executor = "src/Guardrails.Core/Execution/TaskExecutor.cs"
if ((Get-Content $executor -Raw) -notmatch '\bWorkspaceScopeEnforcer\b') {
    Write-Output "$executor does not reference WorkspaceScopeEnforcer - the detect seam was not wired into the attempt path (enforcement would never run in production)."
    exit 1
}
exit 0
