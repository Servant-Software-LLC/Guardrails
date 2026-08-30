# catches: an SSOT that still describes a single-runner world after the openai-compat runner shipped -
#          invariant 4 says the contract lands in the SAME change as the code, and a stale SSOT is how the
#          next reader (and the next breakdown) builds against a harness that no longer exists.
#
# DOCUMENTATION DELIVERABLE - two consequences, both deliberate:
#   1. EXEMPT from the two-sided sample pair (#468/#302): you cannot synthesize a meaningful "invalid"
#      sample of a design document. The PRECEDENT check is the mandatory substitute, and it is applied:
#      every token below is demanded in the form this document ALREADY uses for the same kind of fact -
#      backticked identifiers, bare GR codes in the validation table. Sibling precedent: the existing
#      GR2044 / GR2049 rows and the backticked key names in the canonical promptRunners block.
#   2. Comments are STRIPPED before scanning. An HTML comment satisfies a naive grep exactly as a code
#      comment does, and a contract recorded only inside <!-- --> is not recorded at all.
#
# MEASURED BASELINES (#478), counted against the real file at authoring time, case-sensitively:
#   GR2066                 0     GR2067                0
#   contextTokens          0     apiKeyEnv             0
#   ServesRoles            0     NeedsContainmentHook  0
#   PromptRole             0     tool_calls            0
# Two tokens are DELIBERATELY NOT required, because they are already present and a clause green on
# arrival certifies nothing:
#   openai-compat          6     <- already used when naming the reserved kind
#   GR2065                 1     <- the DiagnosticCodes allocation marker already names it
$ErrorActionPreference = 'Continue'

$path = 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $path)) {
    Write-Output "PRECONDITION: $path is missing - every clause below would crash."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw
# Strip HTML comments so a contract "recorded" inside <!-- --> does not satisfy the scan.
$text = [regex]::Replace($raw, '(?s)<!--.*?-->', '')

$required = @{
    'GR2066'               = 'the action-reachability error code and its five routes'
    'GR2067'               = 'the undeclared-strength / unreachable-block warning'
    'contextTokens'        = 'the required context-window key on an openai-compat block'
    'apiKeyEnv'            = 'the env-var NAME key (never the secret itself)'
    'ServesRoles'          = 'the build fact declaring which roles a kind serves'
    'NeedsContainmentHook' = 'the build fact the containment splice is now conditioned on'
    'PromptRole'           = 'the required PromptInvocation.Role contract'
    'tool_calls'           = 'the tool-capability probe and the false green it closes'
}

$failures = @()
foreach ($token in ($required.Keys | Sort-Object)) {
    if ($text -notmatch [regex]::Escape($token)) {
        $failures += "MISSING: '$token' - $($required[$token]). Baseline 0 on the starting tree, so this is genuinely new content, not a pre-satisfied clause."
    }
}

# The new section 9.8 must exist as a HEADING, not merely as a cross-reference someone wrote in passing.
if ($text -notmatch '(?m)^#+\s*9\.8\b') {
    $failures += "MISSING: no section 9.8 heading. The plan's section 12 item 8 asks for a NEW section '9.8 - The openai-compat runner (#223)' carrying the block schema, role gate, wire mapping, containment primitive, failure taxonomy, verdict transcription and the preflight."
}

if ($failures.Count -gt 0) {
    Write-Output "=== SSOT does not yet record the openai-compat contract ($($failures.Count) gap(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "Write for a reader who has not read plan 28. Do NOT reshape the document away from its own conventions to satisfy a pattern - if a token belongs somewhere other than where this check looks, say so rather than forcing it."
    exit 1
}

Write-Output "SSOT records the openai-compat contract: all 8 new tokens present outside comments, section 9.8 exists."
exit 0
