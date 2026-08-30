# catches: an SSOT that "records" the openai-compat block by naming its keys ANYWHERE in a 5000-line
#          document instead of putting them in the canonical block. Measured during review: the first
#          version of this task's guardrail was satisfied by THREE LINES of prose appending a stub
#          heading and one sentence listing every required token - exit 0, zero contract recorded
#          (Probe B operator 18). This check closes that by demanding a STRUCTURAL POSITION: each key
#          must appear INSIDE the `"promptRunners": { ... }` region the canonical-schema sentinel
#          marks, which is the region task 26 mirrors byte-for-byte and SchemaDriftTests parses.
#
# DOCUMENTATION DELIVERABLE - exempt from the two-sided sample pair (#468), since no meaningful
#          "invalid" sample of a design document exists. The PRECEDENT check is the substitute and is
#          applied: every key is demanded in the form the block ALREADY uses for an absent optional
#          key. Sibling precedent, read out of the block itself: the existing `model`, `effort`,
#          `costly` and `strength` keys are each shown with a `null` default and a trailing comment.
#
# MEASURED BASELINES (#478), counted INSIDE the canonical region on the starting tree:
#   endpoint 0 · contextTokens 0 · apiKeyEnv 0 · wire 0 · engine 0
# Deliberately NOT required anywhere in this file: `openai-compat` (already 6 occurrences document-wide)
#   and `GR2065` (already 1 - the DiagnosticCodes allocation marker). A clause green on arrival
#   certifies nothing, so neither is used as evidence here.
$ErrorActionPreference = 'Continue'

$path = 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $path)) {
    Write-Output "PRECONDITION: $path is missing - every clause below would crash."
    exit 1
}

# Locate the canonical region the way the SSOT's own sentinel comment defines it: from the
# `"promptRunners":` line through the matching close at the SAME indent. Layout-independent, and it
# survives the document being re-fenced or re-sectioned.
$lines = Get-Content -LiteralPath $path
$region = $null
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^(\s*)"promptRunners"\s*:') {
        $indent = $Matches[1]
        $body = New-Object System.Collections.Generic.List[string]
        $body.Add($lines[$i])
        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            $body.Add($lines[$j])
            if ($lines[$j] -match ('^' + [regex]::Escape($indent) + '\}')) {
                $region = ($body -join "`n")
                break
            }
        }
        break
    }
}

if ($null -eq $region) {
    Write-Output "PRECONDITION: no `"promptRunners`": region found in $path (or it never closes at its own indent). The canonical block is what this task must edit; without it there is nothing to check."
    exit 1
}

# Strip HTML comments so a key "recorded" only inside <!-- --> does not satisfy the scan.
$scan = [regex]::Replace($region, '(?s)<!--.*?-->', '')

$required = @{
    'endpoint'      = 'the absolute http/https base URL (REQUIRED for this kind)'
    'contextTokens' = 'the context-window bound the section 6.1 refusal is computed against'
    'apiKeyEnv'     = 'the NAME of an env var holding a bearer token - never the token itself'
    'wire'          = 'the verbatim request-body passthrough'
    'engine'        = 'the operator-facing remedy-text hint (never a code path)'
}

$failures = @()
foreach ($key in ($required.Keys | Sort-Object)) {
    if ($scan -notmatch ('"' + [regex]::Escape($key) + '"')) {
        $failures += "MISSING FROM THE CANONICAL BLOCK: `"$key`" - $($required[$key]). Baseline 0 inside the region. Naming it elsewhere in the document does not count: task 26 mirrors THIS region byte-for-byte and SchemaDriftTests parses it, so a key outside it reaches neither."
    }
}

# The block must still say which kinds are real. Two are now implemented, and a reader who is told
# only that `openai-compat` exists cannot tell whether it can actually be constructed.
if ($scan -notmatch 'IMPLEMENTED') {
    $failures += "THE KIND COMMENT WAS NOT UPDATED: the canonical block should state which kinds are IMPLEMENTED. As of this plan that is `claude` AND `openai-compat`; the others remain reserved names that validate clean and then throw at registry construction."
}

if ($failures.Count -gt 0) {
    Write-Output "=== The canonical promptRunners block does not yet carry the openai-compat surface ($($failures.Count) gap(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "Each key belongs INSIDE the `"promptRunners`": { ... } region, shown in its absent (null) state, following the form the block already uses for `model` / `effort` / `costly` / `strength`. Do NOT reshape the document to satisfy a pattern - if a key genuinely belongs elsewhere, say so rather than forcing it."
    exit 1
}

Write-Output "Canonical block carries all 5 openai-compat keys and names the implemented kinds."
exit 0
