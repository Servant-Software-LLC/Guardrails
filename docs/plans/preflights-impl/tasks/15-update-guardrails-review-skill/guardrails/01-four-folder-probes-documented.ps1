# catches: the guardrails-review migration that never documents probing the four folders as BLOCKERs or the
#          advisory live-probe WARN. Lower-bound presence grep (a mention satisfies it - real verification is
#          the D10 golden + human review), scoped to the one skill directory this task owns.
$dir = ".claude/skills/guardrails-review"
$all = (Get-ChildItem $dir -Recurse -File -Include *.md | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
if ($all -notmatch '<plan>/guardrails/|Terminal Gate') {
    Write-Output "guardrails-review skill does not document probing the terminal <plan>/guardrails/ folder (the re-homed GR2018 BLOCKER)"
    exit 1
}
if ($all -notmatch '<plan>/preflights/|preflight') {
    Write-Output "guardrails-review skill does not document probing the plan-level/task-level preflights folders"
    exit 1
}
if ($all -notmatch '(?i)advisory|WARN') {
    Write-Output "guardrails-review skill does not document the live-probe guidance as an ADVISORY WARN (not a BLOCK)"
    exit 1
}
exit 0
