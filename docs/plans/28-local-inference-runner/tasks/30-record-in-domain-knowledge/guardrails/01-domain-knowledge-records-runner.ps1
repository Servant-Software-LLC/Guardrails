# catches: a domain-knowledge skill that still tells every future agent there is exactly one implemented
#          runner kind - and, equally, one that "records" the change by naming every token in a single
#          sentence. Measured on this plan's sibling task during review (Probe B operator 18): three
#          lines of prose satisfied a presence-only version of this check with exit 0. This skill is
#          loaded by every agent working in this repo, so a stale or token-shaped entry propagates a
#          wrong mental model into every later plan - the SELF-UPDATING clause exists for this change.
#
# DOCUMENTATION DELIVERABLE - exempt from the two-sided sample pair (#468); PRECEDENT check applied
#          instead: every token is demanded in the form this skill ALREADY uses for the same kind of
#          fact - backticked identifiers inline in prose, under a bullet. Sibling precedent: the
#          existing entries naming `GR2028` and `PromptRunnerKind` in the same section.
#          Comments are STRIPPED before scanning.
#
# MEASURED BASELINES (#478), case-sensitive, against the real file at authoring time:
#   PromptRole 0 · ServesRoles 0 · tool_calls 0 · MLX 0 · Advisory 0
# NOT required, because already present and therefore green on arrival:
#   openai-compat 1 (the kind is already listed among the reserved names)
$ErrorActionPreference = 'Continue'

$path = '.claude/skills/guardrails-domain-knowledge/SKILL.md'
if (-not (Test-Path $path)) {
    Write-Output "PRECONDITION: $path is missing - every clause below would crash."
    exit 1
}

$required = @{
    'PromptRole'  = 'the required field that lets a runner refuse work it cannot honestly serve'
    'ServesRoles' = 'the build fact declaring which roles a kind serves (never a config key)'
    'tool_calls'  = 'the tool-capability probe and the accepts-tools-calls-none false green it closes'
    'MLX'         = 'the engine that made the protocol-vs-engine distinction load-bearing'
    'Advisory'    = 'the role gate - v1 serves Guardrail and Advisory ONLY and refuses an Action invocation'
}

$failures = @()
$lineOf = @{}

foreach ($token in ($required.Keys | Sort-Object)) {
    $hit = Select-String -Path $path -Pattern ([regex]::Escape($token)) -CaseSensitive |
           Where-Object { $_.Line -notmatch '^\s*<!--' } |
           Select-Object -First 1
    if (-not $hit) {
        $failures += "MISSING: '$token' - $($required[$token]). Baseline 0 on the starting tree."
    }
    else {
        $lineOf[$token] = $hit.LineNumber
    }
}

# DISTRIBUTION - the clause that kills the one-line mention (operator 18). Five tokens spanning the
# role gate, two build facts, the probe and the engine list cannot honestly share one or two lines.
if ($failures.Count -eq 0) {
    $distinct = ($lineOf.Values | Sort-Object -Unique).Count
    if ($distinct -lt 4) {
        $failures += "TOKENS PRESENT BUT NOT DISTRIBUTED: the $($lineOf.Count) required tokens occupy only $distinct distinct line(s). A knowledge entry that names the role gate, the build facts, the probe and MLX in one sentence is a MENTION, not a record - a future agent reading it will not learn that a local model cannot take a task ACTION, which is the single most consequential fact about v1."
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== domain-knowledge skill does not yet record the openai-compat runner ($($failures.Count) gap(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "Keep it proportionate - this is a knowledge skill, not a copy of the SSOT. Point at SSOT section 9.8 for the full contract. Do NOT reword the skill away from its own conventions to satisfy a pattern."
    exit 1
}

Write-Output "domain-knowledge skill records the runner: 5 tokens across $(($lineOf.Values | Sort-Object -Unique).Count) distinct lines."
exit 0
