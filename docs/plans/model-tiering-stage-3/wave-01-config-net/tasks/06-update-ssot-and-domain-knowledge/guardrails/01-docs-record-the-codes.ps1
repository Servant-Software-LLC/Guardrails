# catches: three codes shipped in the code and never written down - the exact rot DoR §13 records
#          happening to this epic's reservations THREE times. Two halves, and the second costs more:
#          (a) the codes are not documented with their severity, so a user who meets GR2052 has
#          nowhere to look it up; (b) a document still advertises GR2051-GR2053 as reserved, so the
#          next allocator takes a number that is already in use.
#
# REWRITTEN AFTER AN INDEPENDENT ADVERSARIAL PASS, which broke the first draft in BOTH directions at
# once - and that combination, not either half, is the reason this file looks the way it does:
#   * TOO STRICT: a genuinely correct implementation drew NINE false reds. The claim pattern included
#     `reserved[- ]by[- ]name`, which is the phrase BOTH target documents actually use and the phrase
#     the prompt itself blesses - so the one sanctioned phrasing could never pass. A +-1 line window
#     then swallowed the neighbouring line that legitimately keeps GR2054 reserved.
#   * TOO LOOSE: appending the single line `GR2051 GR2052 GR2053 warning` to each file took the
#     guardrail from nine problems to exit 0. Each (a) clause tested a LINE independently, and one
#     line can carry all three codes.
# Together those form a gradient: an agent eating nine false reds for writing prose learns to stop
# writing prose, and the shortest path to green is precisely the junk line. A guardrail that is strict
# on correct work and loose on wrong work does not merely fail to help - it AIMS at its own hole.
#
# SOURCE-SHAPE OVER DOCUMENTATION - the #468 sample-pair requirement is EXEMPT (no meaningful invalid
# sample of a design document exists), so the PRECEDENT check is the mandatory substitute and was
# applied: GR2049/GR2050 are documented in both targets, and both state severity with "warn"/"warning",
# so the clause accepts either spelling rather than pinning one the documents do not use.
$ErrorActionPreference = 'Continue'
$failures = @()

$taken = @('GR2051', 'GR2052', 'GR2053')

# The constant NAME each code must be documented ALONGSIDE. Requiring the pair on one line is what
# closes the junk-line hole: a bare code+severity token dump no longer satisfies anything.
$names = @{ 'GR2051' = 'NonRoutableBlockIsDefault'; 'GR2052' = 'CostlyBlockRoutingInert'; 'GR2053' = 'PinAndTierCoexist' }

# Documents that must DOCUMENT the codes (clause a) and retire them (clauses b, c).
$documentSubjects = @(
    'docs/plans/02-schemas-and-contracts.md',
    '.claude/skills/guardrails-domain-knowledge/SKILL.md'
)
# The DoR must only RETIRE them (clauses b, c) - it already documents them in its §13.2 reservation
# table, so a clause-(a) check there would be PRE-SATISFIED on arrival and certify nothing (#478).
$retireOnlySubjects = @('docs/plans/17-model-tiering.md')

foreach ($subject in ($documentSubjects + $retireOnlySubjects)) {
    # PRECONDITION - every clause below would crash on a missing subject.
    if (-not (Test-Path $subject)) {
        Write-Output "$subject not found - cannot check the documentation"
        exit 1
    }
    $lines      = Get-Content -Path $subject
    $mustDocument = $documentSubjects -contains $subject

    # (a) DOCUMENTED: the code and its CONSTANT NAME on one line, with severity, and the three on
    # DISTINCT lines. MEASURED BASELINE 2026-08-22: 0 in both document subjects for all three.
    if ($mustDocument) {
        $usedLines = @{}
        foreach ($code in $taken) {
            $hits = @(0..($lines.Count - 1) | Where-Object {
                $lines[$_] -match $code -and $lines[$_] -match $names[$code] -and $lines[$_] -imatch 'warn'
            })
            $fresh = @($hits | Where-Object { -not $usedLines.ContainsKey($_) })
            if ($fresh.Count -lt 1) {
                if ($hits.Count -gt 0) {
                    $failures += "$subject : $code is documented only on a line already counted for another code - give each of the three its own entry, not one shared token dump."
                } else {
                    $failures += "$subject : $code is not documented - no line carries '$code', its constant name '$($names[$code])' AND 'warn'/'warning' together. Record it where this document already records GR2047-GR2050."
                }
            } else {
                $usedLines[$fresh[0]] = $true
            }
        }
    }

    # (b) NO LINE may put a retired code beside the vocabulary of reservation. Same line, no window, no
    # tense heuristic - see the rewrite note above for why both of those were removed. (?<!-) so
    # "next-free" is not read as reservation vocabulary.
    foreach ($code in $taken) {
        foreach ($i in 0..($lines.Count - 1)) {
            if ($lines[$i] -notmatch $code) { continue }
            # SKIP a line that is itself a DOCUMENTATION entry for this code (clause (a)'s shape: the
            # code, its constant name, and a severity). Found by re-running Probe C against this fix:
            # GR2051's meaning is literally "untagged work lands on the RESERVED model", so a correct
            # severity-table row explaining the code would trip the reservation ban. A row that
            # documents a code is not a claim that the code is available - it is the opposite.
            if ($lines[$i] -match $names[$code] -and $lines[$i] -imatch 'warn') { continue }
            if ($lines[$i] -imatch '(?<!-)\b(reserved|free)\b') {
                $failures += "$subject : line $($i + 1) puts $code on the same line as 'reserved'/'free' - wave 1 TOOK that code, so the next allocator reading this collides with it. Retire it in words that cannot be misread: say TAKEN or ALLOCATED, and keep any sentence that still reserves GR2054 on a line of its own. Offending line: $($lines[$i].Trim())"
                break
            }
        }
    }

    # (c) GR2054 must SURVIVE as reserved - the v2 #227 probes code, which nothing in this plan takes.
    # DECLARED EXCEPTION (#478): this clause is GREEN ON ARRIVAL by design. It is a REGRESSION clause -
    # it guards against retiring GR2051-GR2053 sweeping GR2054 away with them - so its baseline is
    # necessarily nonzero (measured 2026-08-22: present in all three subjects).
    $stillReserved = @($lines | Where-Object { $_ -match 'GR2054' -and $_ -imatch '(?<!-)\b(reserved|free)\b' })
    if ($stillReserved.Count -lt 1) {
        $failures += "$subject : nothing reserves GR2054 any more - retiring GR2051-GR2053 must not sweep away the one code that IS still reserved."
    }
}

$failures = @($failures | Select-Object -Unique)
if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== documentation: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
