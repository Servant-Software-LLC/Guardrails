# catches: an allocation that compiles but is wrong in a way the compiler cannot see - a code taken at
#          the wrong NUMBER (GR2065 instead of the reserved gap), the SAME literal assigned to two
#          constants (compiles cleanly, and is exactly the #175 shape a build never catches), the
#          reservation block still advertising GR2051-GR2053 as free so the next allocator collides
#          with them, GR2054 quietly consumed along with them, or the next-free counter silently
#          advanced past codes nobody took.
#
# WHY A SOURCE-SHAPE CHECK HERE, and not a test (#468 demotion order). Two of the four properties below
# have NO runtime proxy at all: a reservation COMMENT is invisible to a running program, and the
# next-free marker is a comment too. The remaining two (the declarations exist; no literal is
# duplicated) are file facts about an allocation, asserted here only because they live in the same
# statement as the comment properties. The constants' VALUES get their runtime proof downstream - tasks
# 02/04 assert that the validator emits a diagnostic carrying each code.
#
# Parameterized subject so this script is author-time smoke-testable (#302, Step 7.0d) against the
# committed samples/ pair; the harness invokes it with no arguments, so the default is the real file.
param([string]$SubjectPath = 'src/Guardrails.Core/Loading/DiagnosticCodes.cs')
$ErrorActionPreference = 'Continue'

# PRECONDITION - the one legitimate early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $SubjectPath)) {
    Write-Output "$SubjectPath not found - cannot check the allocation"
    exit 1
}
$content = Get-Content -Raw -Path $SubjectPath
$lines   = Get-Content -Path $SubjectPath

# ACCUMULATE (#478): one distinguishable message per clause, dumped once at the end, so ONE attempt
# learns every gap instead of discovering them one retry at a time.
$failures = @()

$codes = [ordered]@{
    'NonRoutableBlockIsDefault' = 'GR2051'
    'CostlyBlockRoutingInert'   = 'GR2052'
    'PinAndTierCoexist'         = 'GR2053'
}

foreach ($name in $codes.Keys) {
    $value = $codes[$name]

    # MEASURED BASELINE 2026-08-22 against the real file: 0 for all three. (A bare '$value' would have
    # measured 2 for GR2051 - it already appears in the "deliberately NOT taken" note and in the
    # reservation line - which is exactly why the clause is anchored on the DECLARATION form.)
    $decl = [regex]::Matches($content, ('\b' + [regex]::Escape($name) + '\s*=\s*"' + [regex]::Escape($value) + '"'))
    if ($decl.Count -lt 1) {
        $failures += "$name is not declared with the literal `"$value`" - the code must be taken at the number DoR 13.2 reserved for it, not at the next-free counter"
    }

    # No duplicate literal. Two constants sharing a value compiles cleanly; only this clause sees it.
    $assign = [regex]::Matches($content, ('=\s*"' + [regex]::Escape($value) + '"'))
    if ($assign.Count -gt 1) {
        $failures += "the literal `"$value`" is assigned $($assign.Count) times - two constants carrying the same code compiles cleanly and silently makes one of them unreachable to a reader (#175)"
    }
}

# The reservation block must no longer advertise the three codes as free.
#
# ANCHORED ON THE ACTIVE CLAIM, NOT ON THE BARE WORD (#470: anchor on a USE, not a mention). An earlier
# draft flagged any line containing both "RESERVED" and a code, and the author-time smoke-test (#302)
# immediately caught it false-firing on a comment that merely DESCRIBED the rule. A historical note
# ("GR2051 WAS reserved until Stage 3 took it") is legitimate and must stay legal; only a live claim
# that the code IS reserved or free is the defect.
#
# A LINE WINDOW, sized to how a comment actually WRAPS - not a character window.
#
# The claim spans lines in the real file (the phrase on one line, GR2052/GR2053 on the next), so a
# strict same-line scan misses two of the three codes. But a wide CHARACTER window is worse: the
# author-time smoke-test (#302) caught a 400-char window false-REDDING the VALID sample, because in a
# compact file the legitimate "GR2054 remains RESERVED" line sits within 400 chars of the three
# constant declarations. A false red is the expensive direction - it dead-ends every attempt on work
# that is already correct - so the window is the claim's line plus one line either side, which is what
# a wrapped comment spans and nothing more.
$claimPattern = '(?i)\b(remain|remains|still)\s+(reserved|free)\b'
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notmatch $claimPattern) { continue }
    $lo     = [Math]::Max(0, $i - 1)
    $hi     = [Math]::Min($lines.Count - 1, $i + 2)   # the claim line + up to two wrapped continuations
    $window = ($lines[$lo..$hi] -join "`n")
    foreach ($taken in @('GR2051', 'GR2052', 'GR2053')) {
        if ($window -match [regex]::Escape($taken)) {
            $failures += "$taken is still claimed as reserved-or-free (line $($i + 1): '$($lines[$i].Trim())') - this task TOOK that code, so the next allocator reading that claim will collide with it. Retire the three from the reservation block; a PAST-TENSE historical note is fine, a live claim is not."
        }
    }
}
$failures = @($failures | Select-Object -Unique)   # one message per code, not one per overlapping window

# GR2054 must SURVIVE as reserved - it is the v2 probes code (#227) and nothing in this plan takes it.
# Baseline 2026-08-22: 4 occurrences of GR2054, at least one on a RESERVED line.
$stillReserved = @($lines | Where-Object { $_ -imatch 'RESERVED' -and $_ -match 'GR2054' })
if ($stillReserved.Count -lt 1) {
    $failures += "no line reserves GR2054 any more - it is the v2 #227 probes code and must stay reserved by name; retiring GR2051-GR2053 must not sweep it away with them"
}

# The next-free counter must NOT move: these three were gaps BELOW it.
# Measured baseline 2026-08-22: exactly 1 occurrence of this line.
if (($content -match 'CURRENT next-free code:\s*GR2065') -eq $false) {
    $failures += "the 'CURRENT next-free code: GR2065' marker is gone or has moved - GR2051-GR2053 are gaps BELOW the counter, so taking them must not advance it. Advancing it silently renumbers somebody else's next code."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== code allocation: $($failures.Count) problem(s) in $SubjectPath ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
