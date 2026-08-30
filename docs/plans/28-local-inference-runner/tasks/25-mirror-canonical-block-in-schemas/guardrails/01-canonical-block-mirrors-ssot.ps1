# catches: a mirror that PARAPHRASES the canonical promptRunners block instead of reproducing it. The two
#          halves are bound by the canonical-schema:promptRunners sentinel and a drift test, so a
#          near-copy is exactly the failure this guardrail exists for - it reads fine and diverges the
#          moment either side is edited.
#
# This is NOT a source-shape proxy: it is a BYTE COMPARISON of the two mirror halves, which is the
# property itself. No sample pair is needed or possible - the check IS the equality.
#
# HOW THE REGION IS LOCATED, and why the obvious way is wrong. The first cut of this guardrail took
# "the first fenced block at or after the sentinel" in both files and RED-FAILED on the untouched tree,
# because the two halves are laid out DIFFERENTLY:
#   - the SSOT's sentinel (02-schemas-and-contracts.md:218) sits BELOW its block and says so in words -
#     "the `"promptRunners": { … }` block ABOVE ... from its `"promptRunners":` line through its matching
#     close, leading 2-space indent included";
#   - the mirror wraps its block BETWEEN a paired `canonical-schema:promptRunners` /
#     `/canonical-schema:promptRunners` sentinel.
# A fence-relative search therefore found an unrelated later block in the SSOT and reported drift on a
# tree where none existed - a false red that would have dead-ended task 25 on every attempt. Caught by
# executing this guardrail against the starting tree before shipping it (Step 7.0a), not by reading it.
#
# So both sides are located the way the SSOT's own comment defines the region: from the
# `"promptRunners":` line through the matching close at the SAME indent. That is layout-independent and
# survives either file being re-fenced or re-sectioned.
#
# GREEN ON ARRIVAL, AND LEGITIMATELY SO - read this before flagging it. Executed against the untouched
# tree this exits 0 (both regions are 24 lines and already identical), because neither task 24 nor task
# 25 has run. That is not the vacuous-guardrail smell Step 7.0a hunts: this is a RELATIONAL invariant,
# not a presence check. Task 24 edits the SSOT region and thereby breaks the equality; this guardrail
# then goes RED and stays red until task 25 re-syncs the mirror. Its red window is precisely the
# interval it exists to police.
# Smoke-tested two-sided at authoring time (#302): exit 0 on the in-sync tree, exit 1 on a scratch copy
# with one comment token inserted into the mirror - and the mutation was verified to have actually
# changed the file first, because a negative sample that does not bite is a green with no information.
$ErrorActionPreference = 'Continue'

$ssotPath = 'docs/plans/02-schemas-and-contracts.md'
$mirrorPath = '.claude/skills/plan-breakdown/references/schemas.md'

foreach ($p in @($ssotPath, $mirrorPath)) {
    if (-not (Test-Path $p)) {
        Write-Output "PRECONDITION: $p is missing - the mirror cannot be compared."
        exit 1
    }
}

function Get-PromptRunnersRegion([string]$path) {
    $lines = Get-Content -LiteralPath $path
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^(\s*)"promptRunners"\s*:') {
            $indent = $Matches[1]
            $body = New-Object System.Collections.Generic.List[string]
            $body.Add($lines[$i].TrimEnd())
            for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                $body.Add($lines[$j].TrimEnd())
                # The matching close is the first later line at the SAME indent starting with '}'.
                if ($lines[$j] -match ('^' + [regex]::Escape($indent) + '\}')) {
                    return ($body -join "`n")
                }
            }
            return $null   # opened but never closed at that indent
        }
    }
    return $null
}

$ssot = Get-PromptRunnersRegion $ssotPath
$mirror = Get-PromptRunnersRegion $mirrorPath

$failures = @()
if ($null -eq $ssot) {
    $failures += "PRECONDITION: no `"promptRunners`": region found (or it never closes at its own indent) in $ssotPath."
}
if ($null -eq $mirror) {
    $failures += "PRECONDITION: no `"promptRunners`": region found (or it never closes at its own indent) in $mirrorPath."
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

if ($ssot -ne $mirror) {
    Write-Output "MIRROR DRIFT: the canonical promptRunners block differs between the SSOT and the plan-breakdown schemas reference."
    Write-Output ""
    $a = $ssot -split "`n"
    $b = $mirror -split "`n"
    Write-Output ("  SSOT region  : " + $a.Count + " lines")
    Write-Output ("  mirror region: " + $b.Count + " lines")
    Write-Output ""
    Write-Output "--- first differing line ---"
    $max = [Math]::Max($a.Count, $b.Count)
    for ($i = 0; $i -lt $max; $i++) {
        $x = if ($i -lt $a.Count) { $a[$i] } else { '<end of region>' }
        $y = if ($i -lt $b.Count) { $b[$i] } else { '<end of region>' }
        if ($x -ne $y) {
            Write-Output ("  line " + ($i + 1) + " SSOT   : " + $x)
            Write-Output ("  line " + ($i + 1) + " mirror : " + $y)
            break
        }
    }
    Write-Output ""
    Write-Output "These two are bound byte-for-byte by a drift test. Reproduce the SSOT region exactly - including comments, the leading indent, and the absent (null) states of endpoint, contextTokens, apiKeyEnv, wire and engine. Do not paraphrase and do not reformat."
    exit 1
}

Write-Output ("Canonical promptRunners region matches the SSOT byte for byte (" + ($ssot -split "`n").Count + " lines).")
exit 0
