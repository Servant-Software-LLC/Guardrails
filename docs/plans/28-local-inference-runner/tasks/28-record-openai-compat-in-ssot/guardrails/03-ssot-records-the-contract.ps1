# catches: an SSOT that names every required token on ONE line of prose. Measured during this plan's
#          review (Probe B operator 18): three appended lines - a stub `### 9.8` heading plus one
#          sentence listing all eight tokens - took the first version of this guardrail to exit 0 with
#          zero contract recorded. So presence alone is not the bar here; the tokens must be
#          DISTRIBUTED, because a contract described in one sentence is a mention, not a record.
#
# DOCUMENTATION DELIVERABLE - exempt from the two-sided sample pair (#468); the PRECEDENT check is the
#          substitute and is applied: every token is demanded in the form this document ALREADY uses
#          for the same kind of fact - bare GR codes in the validation table, backticked identifiers in
#          section 9 prose. Sibling precedent: the existing GR2044 / GR2049 rows and the `ServesRoles`-
#          shaped build-fact bullets already in section 9.
#          Comments are STRIPPED before scanning: a contract recorded inside <!-- --> is not recorded.
#
# MEASURED BASELINES (#478), counted case-sensitively against the real file at authoring time:
#   GR2066 0 · GR2067 0 · ServesRoles 0 · NeedsContainmentHook 0 · PromptRole 0 · tool_calls 0
# NOT required, because already present and a clause green on arrival certifies nothing:
#   openai-compat 6 · GR2065 1 (the DiagnosticCodes allocation marker)
# The config KEYS are checked by 01-canonical-block-carries-the-keys.ps1 instead, which demands a
#   structural position rather than mere presence - do not duplicate them here.
$ErrorActionPreference = 'Continue'

$path = 'docs/plans/02-schemas-and-contracts.md'
if (-not (Test-Path $path)) {
    Write-Output "PRECONDITION: $path is missing - every clause below would crash."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw
$text = [regex]::Replace($raw, '(?s)<!--.*?-->', '')

$required = @{
    'GR2066'               = 'the action-reachability error and its five routes'
    'GR2067'               = 'the undeclared-strength / unreachable-block warning'
    'ServesRoles'          = 'the build fact declaring which roles a kind serves'
    'NeedsContainmentHook' = 'the build fact the containment splice is now conditioned on'
    'PromptRole'           = 'the required PromptInvocation.Role contract'
    'tool_calls'           = 'the tool-capability probe and the false green it closes'
}

$failures = @()
$lineOf = @{}

foreach ($token in ($required.Keys | Sort-Object)) {
    $hit = Select-String -Path $path -Pattern ([regex]::Escape($token)) -CaseSensitive |
           Where-Object { $_.Line -notmatch '^\s*<!--' } |
           Select-Object -First 1
    if (-not $hit) {
        $failures += "MISSING: '$token' - $($required[$token]). Baseline 0 on the starting tree, so this is genuinely new content, not a pre-satisfied clause."
    }
    else {
        $lineOf[$token] = $hit.LineNumber
    }
}

# The new section 9.8 must exist as a HEADING, not a cross-reference someone wrote in passing.
if ($text -notmatch '(?m)^#+\s*9\.8\b') {
    $failures += "MISSING: no section 9.8 heading. The plan's section 12 item 8 asks for a NEW section '9.8 - The openai-compat runner (#223)' carrying the block schema, role gate, wire mapping, containment primitive, failure taxonomy, verdict transcription, the preflight and the tool-capability probe."
}

# DISTRIBUTION, the clause that kills the one-line mention. Six tokens describing five different
# subsystems cannot honestly share two lines of prose; requiring distinct lines costs a correct
# implementation nothing and costs the gaming edit everything.
if ($failures.Count -eq 0) {
    $distinct = ($lineOf.Values | Sort-Object -Unique).Count
    if ($distinct -lt 5) {
        $failures += "TOKENS PRESENT BUT NOT DISTRIBUTED: the $($lineOf.Count) required tokens occupy only $distinct distinct line(s). They describe five different subsystems - the reachability gate, the strength warning, two build facts, the Role contract and the tool-capability probe - so a record that names them all on one or two lines is a MENTION, not a contract. Write each where it belongs in section 9 / 9.8 / the validation table."
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== SSOT does not yet record the openai-compat contract ($($failures.Count) gap(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "Write for a reader who has not read plan 28. Do NOT reshape the document away from its own conventions to satisfy a pattern - if a token belongs somewhere other than where this check looks, say so rather than forcing it."
    exit 1
}

Write-Output "SSOT records the contract: 6 tokens present outside comments across $(($lineOf.Values | Sort-Object -Unique).Count) distinct lines, section 9.8 exists."
exit 0
