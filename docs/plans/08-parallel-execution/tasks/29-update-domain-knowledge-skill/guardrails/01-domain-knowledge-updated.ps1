# catches: the domain-knowledge skill update claimed-done but SKILL.md never reflects plan 08's
#          execution semantics (worktree / write-scope / AI-merge). Scoped to the skill's own SKILL.md.
$skill = ".claude/skills/guardrails-domain-knowledge/SKILL.md"
if (-not (Test-Path $skill)) {
    Write-Output "$skill does not exist"
    exit 1
}
$text = Get-Content $skill -Raw
$needles = @('worktree', 'writeScope', 'AI-merge')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch [regex]::Escape($n)) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "guardrails-domain-knowledge SKILL.md does not reflect the plan-08 term(s): $($missing -join ', ')"
    exit 1
}
exit 0
