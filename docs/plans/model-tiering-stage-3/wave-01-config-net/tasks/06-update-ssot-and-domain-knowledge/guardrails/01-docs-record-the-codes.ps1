# catches: three codes shipped in the code and never written down - the exact rot DoR §13 records
#          happening to this epic's reservations THREE times. Two halves, and the second is the one
#          that actually costs: (a) the codes are not documented with their severity, so a user who
#          meets GR2052 has nowhere to look it up; (b) both documents still advertise GR2051-GR2053 as
#          RESERVED and free, so the next allocator takes a number that is already in use.
#
# SOURCE-SHAPE OVER DOCUMENTATION - the #468 sample-pair requirement is EXEMPT here (you cannot
# synthesize a meaningful invalid sample of a design document), so the PRECEDENT check is the
# mandatory substitute and was applied: every literal token demanded below already appears in these
# same two files for a sibling code. GR2049/GR2050 are documented in both (SSOT 2 and 7 occurrences,
# skill 1 and 2), and both files state a code's severity with the word "warn"/"warning" - so the
# clause accepts EITHER spelling rather than pinning one the document does not use.
$ErrorActionPreference = 'Continue'
$failures = @()

$subjects = @(
    'docs/plans/02-schemas-and-contracts.md',
    '.claude/skills/guardrails-domain-knowledge/SKILL.md'
)
$taken = @('GR2051', 'GR2052', 'GR2053')

foreach ($subject in $subjects) {
    # PRECONDITION - every clause below would crash on a missing subject.
    if (-not (Test-Path $subject)) {
        Write-Output "$subject not found - cannot check the documentation"
        exit 1
    }
    $lines = Get-Content -Path $subject

    # (a) DOCUMENTED, with severity. MEASURED BASELINE 2026-08-22: 0 in BOTH files for all three.
    # The clause is anchored on code-AND-severity on one line precisely because the BARE token is
    # already nonzero on arrival - GR2051 appears 1x in the SSOT and 2x in the skill, in the
    # reservation sentences below. A bare-token clause would have been green before the task ran and
    # certified nothing (#478); this one measures 0.
    foreach ($code in $taken) {
        $documented = @($lines | Where-Object { $_ -match $code -and $_ -imatch 'warn' })
        if ($documented.Count -lt 1) {
            $failures += "$subject : $code is not documented with its severity - no line carries both '$code' and 'warn'/'warning'. Record it where this document already records GR2047-GR2050."
        }
    }

    # (b) NO LIVE RESERVATION CLAIM may still name a code this wave took.
    # Anchored on the active claim, not on the bare word (#470: a USE, not a mention) - a past-tense
    # historical note is legitimate and must stay legal. The window is the claim's line plus one
    # either side, which is what a wrapped sentence spans; a wide character window false-REDS, which
    # is the expensive direction because it dead-ends every attempt on work that is already correct.
    $claimPattern = '(?i)\b(remain|remains|still)\s+(reserved|free)\b|(?i)reserved[- ]by[- ]name'
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch $claimPattern) { continue }
        $lo     = [Math]::Max(0, $i - 1)
        $hi     = [Math]::Min($lines.Count - 1, $i + 2)
        $window = ($lines[$lo..$hi] -join "`n")
        foreach ($code in $taken) {
            if ($window -match $code) {
                $failures += "$subject : $code is still claimed as reserved-or-free (line $($i + 1): '$($lines[$i].Trim())') - wave 1 TOOK that code, so the next allocator reading this will collide. A past-tense note is fine; a live claim is not."
            }
        }
    }

    # (c) GR2054 must SURVIVE as reserved - the v2 #227 probes code, which nothing in this plan takes.
    # Baseline 2026-08-22: present in both files, on a reservation line in each.
    $stillReserved = @($lines | Where-Object { $_ -match $claimPattern -and $_ -match 'GR2054' })
    if ($stillReserved.Count -lt 1) {
        $failures += "$subject : no live statement reserves GR2054 any more - retiring GR2051-GR2053 must not sweep away the one code that IS still reserved."
    }
}

$failures = @($failures | Select-Object -Unique)   # one message per (file, code), not one per window
if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== documentation: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
