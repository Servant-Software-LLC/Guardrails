# Delete the superseded docs/plans/pilot-seat-model-provenance/ task folder (charter question
# `s3-stale-plan-folder`, answered "Delete it in this stage - this charter supersedes it").
#
# A SCRIPT action, not a prompt: the deliverable is one deterministic filesystem operation with an exactly
# known target. There is no judgement here to spend a model on, and a script is not subject to the tool
# permission layer at all.
#
# WHAT IS BEING DELETED, and why it is worth a task of its own: 57 files across 12 hand-reviewed task
# folders, authored 2026-08-11 and never run. Stage 2 then restructured the exact surfaces they target -
# AttemptProvenance gained runner/kind/tier/tierSource/effort/judge, and its task 04 would author against
# the `resolvedModel` key Stage 2 explicitly REFUSED. The folder still looks runnable, which is the same
# hazard class as everything else in this stage.
#
# WHAT IS NOT: `docs/plans/pilot-seat-model-provenance.md` - the design document, a sibling PATH PREFIX of
# this folder. The charter scopes the FOLDER. A trailing wildcard here would take both, so the path below
# is exact and the delete is guarded on -PathType Container.
$ErrorActionPreference = 'Stop'
$target = 'docs/plans/pilot-seat-model-provenance'

if (-not (Test-Path $target -PathType Container)) {
    # Not an error: the wave ENTRY gate already asserted this folder was present at wave start, so reaching
    # here means it went away during the wave. Say so plainly and let the task's own guardrail render the
    # verdict - it asserts the same absence this branch observed.
    Write-Output "$target is already absent - nothing to delete."
    exit 0
}

$files = @(Get-ChildItem -Path $target -Recurse -File -ErrorAction SilentlyContinue).Count
Remove-Item -Path $target -Recurse -Force

if (Test-Path $target) {
    Write-Output "$target still exists after Remove-Item - the delete did not take (a file lock, or a permission problem). Nothing else in this task can succeed until it does."
    exit 1
}

Write-Output "deleted $target ($files file(s))."
exit 0
