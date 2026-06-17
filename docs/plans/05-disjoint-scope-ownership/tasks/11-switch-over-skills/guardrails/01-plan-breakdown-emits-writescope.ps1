# catches: a skills switch-over that claims to adopt writeScope but never actually changed the
#          plan-breakdown procedure - assert SKILL.md now instructs emitting writeScope. Scoped to
#          the one skill file (grep-scope rule). Necessary-not-sufficient; 03's round-trip proves
#          the emitted folder actually validates.
$skill = ".claude/skills/plan-breakdown/SKILL.md"
if (-not (Test-Path $skill)) {
    Write-Output "$skill does not exist - the plan-breakdown skill is missing."
    exit 1
}
if ((Get-Content $skill -Raw) -notmatch 'writeScope') {
    Write-Output "$skill does not mention writeScope - the plan-breakdown procedure was not switched over to declare a writeScope per task."
    exit 1
}
exit 0
