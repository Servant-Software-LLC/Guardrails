# catches: the /guardrails-review skill claimed updated but the #366 review-report + mark-reviewed
#          --evidence flow was not actually written into SKILL.md — the audit trail would then have no
#          producer. Positive presence check on the one file this task owns. (The "drop unforgeable
#          framing" instruction is a judgment call with no clean structural proxy — a bare -match
#          'unforgeable' would false-fire on wording that explains the marker is NOT unforgeable — so it
#          is intentionally NOT guarded here; see the breakdown report.)
$skill = ".claude/skills/guardrails-review/SKILL.md"
if (-not (Test-Path $skill)) {
    Write-Output "$skill does not exist — the harness-write of the updated skill did not land"
    exit 1
}
$content = Get-Content -Raw -Path $skill
$required = @('plan-hash', 'state/reviews/', '--evidence', 'Plan-Definition-Hash')
foreach ($token in $required) {
    if ($content -notmatch [regex]::Escape($token)) {
        Write-Output "$skill does not reference '$token' — the #366 review-report/evidence flow (doc 16 §4/§12.2) is not documented in the skill"
        exit 1
    }
}
exit 0
