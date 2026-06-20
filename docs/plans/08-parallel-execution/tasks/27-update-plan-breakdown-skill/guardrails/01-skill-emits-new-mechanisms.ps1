# catches: the plan-breakdown skill update claimed-done but SKILL.md never mentions the new mechanisms
#          (writeScope / scope: integration / integrationGate) - scoped to the skill's own SKILL.md
$skill = ".claude/skills/plan-breakdown/SKILL.md"
if (-not (Test-Path $skill)) {
    Write-Output "$skill does not exist"
    exit 1
}
$text = Get-Content $skill -Raw
# Use specific tokens, not the bare substring 'integration' (which matches many unrelated words and
# would pass too easily): scope: "integration" and integrationGate are the load-bearing constructs.
$needles = @('writeScope', 'integrationGate', 'scope: "integration"')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch [regex]::Escape($n)) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "plan-breakdown SKILL.md does not mention the new mechanism term(s): $($missing -join ', ')"
    exit 1
}
exit 0
