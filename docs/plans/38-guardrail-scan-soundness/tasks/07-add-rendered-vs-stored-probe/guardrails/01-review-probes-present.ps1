# catches: a review skill that gained the #428 probe without gaining the ordering correction it depends
#          on - a reviewer told to look for rendered-vs-stored while the same file still tells them that
#          stripping comments is the whole of the preparation. #428 has NO mechanical gate by design
#          (design 38 SS6), so this probe is its only enforcement; landing it half-written leaves the
#          issue with nothing behind it at all.
# DOCUMENTATION target: exempt from the two-sided sample pair (#468) - no meaningful invalid sample of a
#          prose document exists. The compensating controls are the <!-- --> strip below (a required
#          clause over a .md false-PASSES on a commented-out line) and the PRECEDENT check: every literal
#          demanded here is pinned verbatim in this task's own action prompt, so the two cannot drift
#          (GR2026).
# Measured baseline (#478): all three required literals count 0 in this file on the starting tree
#          (verified 2026-09-06 on master a3f3e977).
$ErrorActionPreference = 'Stop'

$problems = New-Object System.Collections.Generic.List[string]
$subject  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { '.claude/skills/guardrails-review/SKILL.md' }

if (-not (Test-Path -LiteralPath $subject -PathType Leaf)) {
    Write-Output "PRECONDITION: $subject does not exist - every clause below would be vacuous."
    exit 1
}

# A required-present clause over a .md must strip HTML comments before matching: <!-- ... --> renders as
# NOTHING, so a commented-out line satisfies a naive grep and the clause false-PASSES. Fenced code blocks
# are deliberately NOT stripped - a fence RENDERS, so a rule stated in a fence is legitimate house style.
$raw   = Get-Content -Raw -LiteralPath $subject
$prose = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
if ($prose -match '<!--') {
    Write-Output "PRECONDITION: $subject contains an unterminated '<!--'. Stripping to EOF would delete the rest of the document, so no clause below can be trusted."
    exit 1
}

$required = @(
    @{ Text = 'the rendered form is not the stored form'
       Why  = "the #428 probe is absent. A guardrail that matches a phrase as it READS rather than as it is STORED silently passes when the target is split across source-line literals - a false-PASS, which is the expensive direction. #428 has no mechanical gate by design, so this probe is its ONLY enforcement." },
    @{ Text = 'ONE source line'
       Why  = "the #428 probe names a trap and offers no remedy. Its first and cheapest fix is 'prefer a distinctive fragment that sits on ONE source line'; without it a reviewer can recognise the defect and not know what to ask for." },
    @{ Text = 'Neutralize string literals BEFORE stripping comments'
       Why  = "this file still implies that stripping comments is the whole of the preparation. A `/*` spelled inside a string literal opens a phantom block comment running to the next `*/` in a later literal, blanking everything between - failing both closed and open. 29 of the 31 committed guardrails that do both operations do them in the defective order (#561)." }
)

foreach ($r in $required) {
    if ($prose -notmatch [regex]::Escape($r.Text)) {
        $problems.Add("[$subject] does not carry '$($r.Text)' - $($r.Why)")
    }
}

if ($problems.Count -gt 0) {
    Write-Output "=== Review probes incomplete ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "guardrails-review carries the #428 rendered-vs-stored probe, its one-source-line remedy, and the #561 preprocessing-order correction."
exit 0
