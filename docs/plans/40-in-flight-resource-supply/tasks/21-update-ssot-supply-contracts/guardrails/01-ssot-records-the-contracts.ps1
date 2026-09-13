# catches: the contract moving without the document that describes it. Each token below
#          measured ZERO occurrences in the subject at authoring time (#478) — the expected
#          answer for a required-present clause, so every one of them has teeth.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = 'docs/plans/02-schemas-and-contracts.md'

if (-not (Test-Path -LiteralPath $subject)) {
    # PRECONDITION: every clause below would crash on a missing subject.
    Write-Output "PRECONDITION: $subject does not exist."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $subject

# An HTML comment RENDERS AS NOTHING, so a token that only appears inside one is invisible text
# and must not satisfy a required-present clause (#468 operator 2).
#
# But a bare lazy `(?s)<!--.*?-->` is WRONG on any document that MENTIONS `<!--` in prose: the
# mention opens a match that runs to the next real `-->` anywhere later. MEASURED on
# .claude/skills/plan-breakdown/SKILL.md - whose line 636 documents this very idiom - the lazy
# form removes 169,334 bytes, 44.8% of the file, in ONE span. The old unterminated-'<!--'
# precondition reported HEALTHY throughout, because a '-->' does exist later: the guard was
# written, executed, and could not fire on the actual defect. This subject is not affected today
# (measured), but the idiom is the same and the next prose mention would reproduce it exactly.
#
# Inside a fenced block or an inline code span, `<!--` is literal text a reader SEES - Markdown
# renders it - so it cannot be an opener. Mask those regions, strip, then UNMASK, which also
# preserves fence content in $doc and so honours the counter-rule that a token documented in a
# usage fence legitimately satisfies a clause.
$maskOpen = [string][char]0x01
$maskClose = [string][char]0x02
$masked = [regex]::Replace($raw, '(?s)```.*?```|`[^`\r\n]*`', {
    param($m) $m.Value.Replace('<!--', $maskOpen).Replace('-->', $maskClose)
})

# Belt and braces: after masking, an opener with no closer really is unterminated.
if ($masked -match '<!--(?![\s\S]*?-->)') {
    Write-Output "PRECONDITION: $subject contains an unterminated '<!--' outside any code span. Refusing to strip to EOF, which would delete the rest of the document over one stray token."
    exit 1
}

$doc = ([regex]::Replace($masked, '(?s)<!--.*?-->', '')).Replace($maskOpen, '<!--').Replace($maskClose, '-->')

$failures = @()

if ($doc -notmatch [regex]::Escape('logs/<runId>/supplied/')) {
    $failures += "MISSING 'logs/<runId>/supplied/' in $subject — the §1 staging-tree contract is not recorded"
}

if ($doc -notmatch [regex]::Escape('supplied[]')) {
    $failures += "MISSING 'supplied[]' in $subject — the §7 run.json provenance section is not recorded"
}

if ($doc -notmatch [regex]::Escape('SuppliedResourcesCommitted')) {
    $failures += "MISSING 'SuppliedResourcesCommitted' in $subject — the §8 observer event is not recorded"
}

if ($doc -notmatch [regex]::Escape('task:<folder>')) {
    $failures += "MISSING 'task:<folder>' in $subject — the §4 record's FIFTH field (by) is not recorded with its value set — a record that cannot name a non-operator supplier makes the §3 auto-resolve condition unimplementable. The token is a VALUE of the field rather than the quoted key, because a double quote inside a PowerShell failure message terminates the string and turns the guardrail into a parse error no retry can fix (measured, this plan, 2026-09-11)"
}

if ($doc -notmatch [regex]::Escape('Supplied-By:')) {
    $failures += "MISSING 'Supplied-By:' in $subject — the commit trailer is not recorded as DERIVED from the by field — the first draft hard-coded Supplied-By-Operator, which is a false statement on any supply the operator did not perform"
}

if ($doc -notmatch [regex]::Escape('own writeScope')) {
    $failures += "MISSING 'own writeScope' in $subject — the DECIDED caller-scoping rule is not recorded — it is a new env-derived authorization contract layered on WriteScope.IsInScope, and the SSOT is where a contract lives"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
