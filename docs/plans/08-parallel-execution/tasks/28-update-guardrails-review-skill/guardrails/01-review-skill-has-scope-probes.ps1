# catches: the guardrails-review skill update claimed-done but SKILL.md never adds the plan-08 probes
#          (writeScope overlap / scope / integrationGate). Scoped to the review skill's own SKILL.md.
$skill = ".claude/skills/guardrails-review/SKILL.md"
if (-not (Test-Path $skill)) {
    Write-Output "$skill does not exist"
    exit 1
}
$text = Get-Content $skill -Raw
$needles = @('writeScope', 'Overlaps', 'integrationGate')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch [regex]::Escape($n)) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "guardrails-review SKILL.md does not add the plan-08 probe term(s): $($missing -join ', ')"
    exit 1
}
exit 0
