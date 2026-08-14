# catches: doctrine that tells the review to FIX an unservable model rather than report it. guardrails-review
#          is read-only by design; a probe that rewrote a plan's runner config would edit the very thing the
#          human is reviewing.
#          The alternation `read-only` was DROPPED: the skill already opens with "Read-only critique by
#          default", so that arm passed against the untouched file and this check certified nothing about the
#          new section. It now requires the reports-not-rewrites phrasing the action prompt actually asks for,
#          which is absent today - so it fails until the task states it.
$ErrorActionPreference = 'Stop'
$file = '.claude/skills/guardrails-review/SKILL.md'
if (-not (Test-Path $file)) { Write-Output "$file not found"; exit 1 }
$c = Get-Content -Raw -Path $file

if ($c -notmatch '(?i)(reports?,? never rewrites|leaves the fix)') {
    Write-Output "$file does not state that the model-availability check REPORTS rather than rewrites - the prompt asks for that phrasing explicitly, and the skill's pre-existing 'Read-only critique' header does not cover this new probe"
    exit 1
}

# Scoped to the new probe rather than the document: the statement has to sit with the model check, not
# anywhere in a 900-line skill that already discusses read-only-ness for other reasons.
$near = [regex]::Match($c, '(?is)model.{0,1200}')
if (-not $near.Success -or $near.Value -notmatch '(?i)(reports?,? never rewrites|leaves the fix)') {
    Write-Output "$file states the reports-not-rewrites rule, but not near the model-availability check it must govern"
    exit 1
}
exit 0
