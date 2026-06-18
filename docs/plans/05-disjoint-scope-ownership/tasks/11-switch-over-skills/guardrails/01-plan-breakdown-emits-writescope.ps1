# catches: a skills switch-over that claims to adopt writeScope but never actually changed the
#          plan-breakdown procedure - assert SKILL.md now instructs emitting writeScope AND no longer
#          instructs the retired triad as the test-protection pattern. The writeScope-present check
#          alone is necessary-not-sufficient: a single writeScope mention passes even if the Step 5
#          triad doctrine still stands. Scoped to the one skill file (grep-scope rule); 03's
#          round-trip proves the emitted folder actually validates.
$skill = ".claude/skills/plan-breakdown/SKILL.md"
if (-not (Test-Path $skill)) {
    Write-Output "$skill does not exist - the plan-breakdown skill is missing."
    exit 1
}
$skillText = Get-Content $skill -Raw
if ($skillText -notmatch 'writeScope') {
    Write-Output "$skill does not mention writeScope - the plan-breakdown procedure was not switched over to declare a writeScope per task."
    exit 1
}
if ($skillText -match 'captureHashes|tests-untouched|restoreOnRetry') {
    Write-Output "$skill still instructs the retired triad (captureHashes/tests-untouched/restoreOnRetry) as the test-protection pattern - the Step 5 doctrine must be replaced by writeScope, not merely supplemented."
    exit 1
}
exit 0
