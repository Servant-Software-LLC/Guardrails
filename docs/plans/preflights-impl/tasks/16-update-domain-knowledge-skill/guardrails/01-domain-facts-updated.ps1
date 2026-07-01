# catches: the domain-knowledge update that never records the moved facts (four-folder model + new outcomes).
#          Lower-bound presence grep (a mention satisfies it - real verification is human review), scoped to
#          the one skill directory this task owns.
$dir = ".claude/skills/guardrails-domain-knowledge"
$all = (Get-ChildItem $dir -Recurse -File -Include *.md | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
if ($all -notmatch '<plan>/preflights/|Full Flight|two-scope|four-folder|four folder') {
    Write-Output "guardrails-domain-knowledge does not record the two-scope four-folder model"
    exit 1
}
if ($all -notmatch 'plan-preflight-failed' -or $all -notmatch 'plan-guardrail-failed' -or $all -notmatch 'task-preflight-failed') {
    Write-Output "guardrails-domain-knowledge does not record all three new outcomes (plan-preflight-failed / task-preflight-failed / plan-guardrail-failed)"
    exit 1
}
exit 0
