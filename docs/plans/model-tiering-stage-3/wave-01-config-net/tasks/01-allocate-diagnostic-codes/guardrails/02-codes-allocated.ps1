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
# ONE LINE, ONE WORD-SET. No window, no proximity, and deliberately no tense heuristic.
#
# REWRITTEN AFTER AN INDEPENDENT ADVERSARIAL PASS, and the history IS the justification. Draft 1
# flagged any line carrying both "RESERVED" and a code - it false-fired on a comment that merely
# DESCRIBED the rule (#470: anchor on a USE, not a mention). Draft 2 added a +-1 line window and a
# past-tense carve-out ("a historical note is fine, a live claim is not") - and false-RED a fully
# correct implementation of the real file, because the note sits next to the line that legitimately
# keeps GR2054 reserved. Two drafts, two false reds on honest work, each one a dead end for every
# attempt.
#
# The lesson is the catalogue's own: when a source-shape check keeps losing, the finding is the
# ARCHETYPE, not the clause. Policing English tense with a regex is unwinnable. So this stops trying to
# tell a live claim from a historical one and states something an author can satisfy without guessing:
# a retired code and the VOCABULARY of reservation may not share a line. The action prompt carries the
# matching instruction - say TAKEN or ALLOCATED, never "reserved"/"free".
#
# It still catches every real site, because all of them are same-line: DiagnosticCodes.cs:848
# ("GR2051-GR2054 also remain RESERVED by name") and :573 ("still reserved in 13.2, still free): GR2051").
foreach ($taken in @('GR2051', 'GR2052', 'GR2053')) {
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch [regex]::Escape($taken)) { continue }
        # (?<!-) is load-bearing, and the re-run of the sample pair is what found it: a bare \bfree\b
        # matches inside "next-FREE", so the "CURRENT next-free code: GR2065" marker line - which this
        # very guardrail requires the task to PRESERVE - would false-red the moment it also mentioned
        # one of the three codes. Clause (d) demanding a line that clause (b) then rejects is the same
        # self-contradiction the +-1 window produced, one layer down.
        if ($lines[$i] -imatch '(?<!-)\b(reserved|free)\b') {
            $failures += "line $($i + 1) puts $taken on the same line as the word 'reserved'/'free' - the next allocator reads that as available and collides with a code this task just took. Retire it in words that cannot be misread: say TAKEN or ALLOCATED, and keep any line that still reserves GR2054 on a line of its own. Offending line: $($lines[$i].Trim())"
            break   # one message per code, not one per matching line
        }
    }
}

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
