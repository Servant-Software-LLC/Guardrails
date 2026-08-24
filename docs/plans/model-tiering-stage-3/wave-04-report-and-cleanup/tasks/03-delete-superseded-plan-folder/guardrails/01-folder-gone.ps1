# catches: the three ways a deletion goes wrong, none of which any build or test suite can see.
#
#          (1) IT DID NOT HAPPEN. The brief is explicit that this guardrail "must assert the folder is
#          GONE, not that some file changed". An action that exited 0 having deleted nothing - or having
#          emptied the folder while leaving the directory - is indistinguishable from success to every
#          other check in this wave.
#
#          (2) A PARTIAL DELETE. `Remove-Item -Recurse` on a tree with a locked file removes some of it.
#          The residue is a folder that still LOOKS like a plan, which is worse than either extreme, so the
#          directory-absent clause is backed by a recursive file count rather than trusting Test-Path alone
#          on a path that may have been half-emptied.
#
#          (3) IT TOOK TOO MUCH. `docs/plans/pilot-seat-model-provenance.md` is a sibling path PREFIX of
#          the target: `Remove-Item docs/plans/pilot-seat-model-provenance*` matches both, and the charter
#          scopes only the FOLDER. `docs/plans/model-tiering-stage-3` is this plan's own directory, which a
#          broader sweep of docs/plans/ would take with it - deleting the plan that is currently executing.
#          Neither loss would fail a build or a test, and the harness's writeScope check does not catch
#          either: the .md and this plan folder are BOTH outside this task's declared scope, so an
#          over-delete is reported as a scope violation without ever naming what was lost. These two
#          clauses name it.
$ErrorActionPreference = 'Continue'
$failures = @()

# MEASURED BASELINE 2026-08-23 against the merged wave-3 HEAD: the target folder is PRESENT with 57 files,
# so clause (1) is correctly RED before this task runs. Clauses (3) are PRESENT-and-must-stay regression
# guards, so their green-on-arrival is EXPECTED and NAMED (#478) - they exist to fail only if this task
# reaches past its target.
$target = 'docs/plans/pilot-seat-model-provenance'

if (Test-Path $target -PathType Container) {
    $remaining = @(Get-ChildItem -Path $target -Recurse -File -ErrorAction SilentlyContinue).Count
    $failures += "$target still exists ($remaining file(s) under it) - the superseded 12-task folder was not deleted. It was authored 2026-08-11 against a provenance contract Stage 2 has since restructured, and it still looks runnable"
}
elseif (Test-Path $target) {
    $failures += "$target exists but is not a directory - something replaced the folder with a file of the same name. Remove it"
}

foreach ($keep in @(
    @('docs/plans/pilot-seat-model-provenance.md',
      'the superseded plan DOCUMENT was deleted too. The charter scopes only the FOLDER; the .md is a path PREFIX of it, so a trailing wildcard takes both. Restore it and delete the directory by its exact path'),
    @('docs/plans/model-tiering-stage-3',
      'this plan''s OWN folder was deleted - a sweep of docs/plans/ took the plan that is currently executing. Restore it'))) {
    if (-not (Test-Path $keep[0])) {
        $failures += "$($keep[0]) no longer exists - $($keep[1])"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== superseded-folder cleanup: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This task deletes exactly one directory, by its exact path: docs/plans/pilot-seat-model-provenance. Nothing beside it."
    exit 1
}
exit 0
