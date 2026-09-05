# catches: a union that dropped a colliding sibling's hunk, left git conflict markers on one of the SIX
#          source files this plan's PARALLEL branches touch, or duplicated a member the AI-merge saw in
#          two regions (#175 - two branches appending the same new member to different parts of one file
#          merge with NO textual conflict marker, leaving a duplicate only the build catches).
#          Branch A (tasks 01/02) writes EscalationLadder.cs + TierResolution.cs; branch B (tasks 03/04)
#          writes JournalModel.cs + TierProvenance.cs + JournalJson.cs; the two join at task 06, which
#          writes TaskExecutor.cs. That is six, and $touched below lists six - the count in this header
#          said five while the code scanned six, which is the sort of drift that later gets "corrected"
#          by deleting a file from the list. They are disjoint by design, and this check is what makes
#          "by design" observable at every union rather than assumed.
#
# scope: "integration" - UNION-SAFE / CONDITIONAL by construction. Every clause GATES on the artifact
#          being present and then verifies it, so it passes trivially at a union where the contributing
#          task has not run yet (#125/#165). It never REQUIRES a contribution to be present.
#
# Required-present baselines (#478): the required-present halves below all sit inside an "if X is
#          present" gate - the union-safe conditional case #478 names explicitly. Measured on the
#          starting tree anyway: `EscalatedFrom` = 0 and `Escalated` (as a TierSource member) = 0 across
#          src/, so every gate is closed before its task runs.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$failures = @()

# ── SHARED PATTERNS - written ONCE so the gate and the count can never drift apart ────────────────
# MODIFIER-TOLERANT (nit c). 'public\s+string\?' broke on 'public required string?' /
# 'public virtual string?' etc., and this gate is scope:"integration" - a false RED here does not fail
# one task, it RED-HALTS EVERY UNION in the plan, so brittleness costs more here than its severity
# suggests. The allowlist is explicit rather than a wildcard: '(?:\w+\s+)*' would also swallow a
# RETURN TYPE and match 'public string? GetEscalatedFrom {' shapes that are not the member.
$escalatedFromDecl = 'public\s+(?:(?:required|virtual|override|sealed|new|abstract|static|readonly|partial)\s+)*string\s*\?\s+EscalatedFrom\s*\{'
# ENUM MEMBER, with an OPTIONAL explicit value (nit b). The old '^\s*Escalated\s*,?\s*$' matched only a
# bare member, so 'Escalated = 3,' duplicated twice slipped through the very duplicate check this
# clause exists to be. The line must still be JUST the member - a property declaration starts with a
# modifier and cannot match.
$escalatedEnumMember = '(?m)^\s*Escalated\s*(?:=\s*[^,\r\n]+?\s*)?,?\s*$'

# ── 1. conflict-marker freedom over every file this plan's parallel branches touch ────────────────
# Line-anchored ours/theirs only - a bare '=======' false-fires on a banner or a setext underline (#187).
$touched = @(
    'src/Guardrails.Core/Prompts/EscalationLadder.cs',
    'src/Guardrails.Core/Prompts/TierResolution.cs',
    'src/Guardrails.Core/Prompts/TierProvenance.cs',
    'src/Guardrails.Core/Journal/JournalModel.cs',
    'src/Guardrails.Core/Journal/JournalJson.cs',
    'src/Guardrails.Core/Execution/TaskExecutor.cs'
)
foreach ($rel in $touched) {
    $path = Join-Path $ws $rel
    if (-not (Test-Path -LiteralPath $path)) { continue }   # not produced at this union yet - nothing to verify
    $content = Get-Content -Raw -LiteralPath $path
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains git conflict markers - the union did not cleanly integrate"
    }
}

# ── 2. contribution-present, CONDITIONAL: a landed token must be a real construct, not a comment ───
$resolution = Join-Path $ws 'src/Guardrails.Core/Prompts/TierResolution.cs'
if (Test-Path -LiteralPath $resolution) {
    $code = Get-Content -Raw -LiteralPath $resolution
    if ($code -match 'EscalatedFrom') {
        # Strip comments before demanding the DECLARATION, so a token surviving only in a doc-comment
        # cannot satisfy the clause (#97/#98).
        $resolutionScan = [regex]::Replace($code, '(?s)/\*.*?\*/', '')
        $resolutionScan = [regex]::Replace($resolutionScan, '(?m)^\s*//.*$', '')
        if ($resolutionScan -notmatch $escalatedFromDecl) {
            $failures += "TierResolution.cs mentions EscalatedFrom but declares no public string? EscalatedFrom property - the union kept the comment and dropped the member"
        }
        $declared = @([regex]::Matches($resolutionScan, $escalatedFromDecl)).Count
        if ($declared -gt 1) {
            $failures += "TierResolution.cs declares EscalatedFrom $declared times - the AI-merge kept two copies of one member (no conflict marker is written for that, #175)"
        }
    }
}

$journalModel = Join-Path $ws 'src/Guardrails.Core/Journal/JournalModel.cs'
if (Test-Path -LiteralPath $journalModel) {
    $code = Get-Content -Raw -LiteralPath $journalModel
    $journalScan = [regex]::Replace($code, '(?s)/\*.*?\*/', '')
    $journalScan = [regex]::Replace($journalScan, '(?m)^\s*//.*$', '')
    $journalScan = [regex]::Replace($journalScan, '(?m)^\s*///.*$', '')
    if ($journalScan -match $escalatedEnumMember) {
        $members = @([regex]::Matches($journalScan, $escalatedEnumMember)).Count
        if ($members -gt 1) {
            $failures += "JournalModel.cs declares the TierSource member Escalated $members times - the AI-merge kept two copies (#175)"
        }
    }
    if ($journalScan -match 'EscalatedFrom') {
        $declared = @([regex]::Matches($journalScan, $escalatedFromDecl)).Count
        if ($declared -lt 1) {
            $failures += "JournalModel.cs mentions EscalatedFrom but declares no public string? EscalatedFrom property on AttemptProvenance - the union kept the comment and dropped the member"
        }
        elseif ($declared -gt 1) {
            $failures += "JournalModel.cs declares EscalatedFrom $declared times - the AI-merge kept two copies of one member (#175)"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== escalation-seam union invariant: $($failures.Count) problem(s) on the merged bytes ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
