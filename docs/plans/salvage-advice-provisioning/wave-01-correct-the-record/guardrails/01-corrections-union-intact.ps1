# catches: an AI-merge of wave 1's five parallel correction tasks that drops a hunk or leaves conflict
#          markers - each task passed alone, but the merged HEAD silently lost a correction.
$failures = @()
$files = @(
  'src/Guardrails.Core/Execution/RetryPolicy.cs',
  'docs/plans/02-schemas-and-contracts.md',
  '.claude/skills/plan-breakdown/SKILL.md',
  '.claude/skills/guardrails-review/SKILL.md',
  '.claude/skills/guardrails-domain-knowledge/SKILL.md'
)
foreach ($f in $files) {
    if (-not (Test-Path $f)) { continue }          # union-safe: absent at this union is fine
    $content = Get-Content -Raw -Path $f
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$f contains git conflict markers - the union did not cleanly integrate"
    }
    if ([string]::IsNullOrWhiteSpace($content)) {
        $failures += "$f is empty in the union - a contribution was lost"
    }
}
if ($failures.Count -gt 0) {
    Write-Output ($failures -join '; ')
    exit 1
}
exit 0
