# catches: a skill edit that adds the availability check but silently drops the JIT-judge case - the
#          settled scope split REQUIRES the deferred half be reported as UNCHECKED, and a review that
#          quietly omits it looks like coverage it does not have.
$ErrorActionPreference = 'Stop'
$file = '.claude/skills/guardrails-review/SKILL.md'
if (-not (Test-Path $file)) { Write-Output "$file not found"; exit 1 }
$c = Get-Content -Raw -Path $file
if ($c -notmatch '(?i)model' -or $c -notmatch '(?i)(availability|configured runner|resolves)') {
    Write-Output "$file does not document the pre-run model-availability check"
    exit 1
}
if ($c -notmatch '(?i)(just-in-time|JIT)') {
    Write-Output "$file never mentions the JIT-resolved judge case - the settled split requires the deferred half be reported as UNCHECKED, not silently omitted"
    exit 1
}
if ($c -notmatch '(?i)(#223|223)') {
    Write-Output "$file does not name #223 as where JIT judge resolution lands - the deferral must point somewhere"
    exit 1
}
exit 0
