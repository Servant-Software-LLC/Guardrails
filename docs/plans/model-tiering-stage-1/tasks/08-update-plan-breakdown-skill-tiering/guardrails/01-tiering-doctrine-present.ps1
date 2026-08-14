# catches: a skill edit that adds classification but never states the GATE - the plan's load-bearing
#          requirement is that an unconfigured plan emits NOTHING, and doctrine that omits it produces
#          breakdowns that silently change every single-model user's output.
$ErrorActionPreference = 'Stop'
$file = '.claude/skills/plan-breakdown/SKILL.md'
if (-not (Test-Path $file)) { Write-Output "$file not found"; exit 1 }
$c = Get-Content -Raw -Path $file
$missing = @()
foreach ($tok in @('tier','easy','medium','hard')) {
    if ($c -notmatch [regex]::Escape($tok)) { $missing += $tok }
}
if ($missing.Count -gt 0) {
    Write-Output ("$file does not document tiering: missing " + ($missing -join ', '))
    exit 1
}
if ($c -notmatch '(?i)byte-identical') {
    Write-Output "$file does not state the Invariant 7 GATE (an unconfigured plan's breakdown must be byte-identical to today) - the classification doctrine without its gate is the expensive half missing"
    exit 1
}
exit 0
