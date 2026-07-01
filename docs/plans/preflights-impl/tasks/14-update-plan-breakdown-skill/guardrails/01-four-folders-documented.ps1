# catches: the plan-breakdown skill migration that never actually documents the four-folder model or the
#          re-homed GR2018 authoring rule. Lower-bound presence grep across the skill dir (a mention
#          satisfies it - the real verification is the D10 golden round-trip + human review), scoped to the
#          one skill directory this task owns.
$dir = ".claude/skills/plan-breakdown"
$all = (Get-ChildItem $dir -Recurse -File -Include *.md | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
if ($all -notmatch '<plan>/preflights/|plan-level.*preflight') {
    Write-Output "plan-breakdown skill does not document the plan-level <plan>/preflights/ folder"
    exit 1
}
if ($all -notmatch '<plan>/guardrails/|Terminal Gate') {
    Write-Output "plan-breakdown skill does not document the plan-level <plan>/guardrails/ (Terminal Gate) folder"
    exit 1
}
if ($all -notmatch 'tasks/<id>/preflights/') {
    Write-Output "plan-breakdown skill does not document the task-level tasks/<id>/preflights/ folder"
    exit 1
}
exit 0
