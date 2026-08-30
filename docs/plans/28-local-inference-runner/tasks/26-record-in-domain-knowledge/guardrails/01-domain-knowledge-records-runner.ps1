# catches: a domain-knowledge skill that still tells every future agent there is exactly one implemented
#          runner kind. This skill is loaded by every agent working in this repo, so a stale entry here
#          propagates a wrong mental model into every later plan - the SELF-UPDATING clause exists for
#          precisely this change.
#
# DOCUMENTATION DELIVERABLE - exempt from the two-sided sample pair (#468), PRECEDENT check applied
#          instead: every token is demanded in the form this skill ALREADY uses for the same kind of
#          fact - backticked identifiers inline in prose. Sibling precedent: the existing entries naming
#          `GR2028` and `PromptRunnerKind` in the same section.
#          Comments are STRIPPED before scanning, so a fact recorded only inside <!-- --> does not count.
#
# MEASURED BASELINES (#478), counted case-sensitively against the real file at authoring time:
#   PromptRole             0     ServesRoles           0
#   Guardrail              0     tool_calls            0
# NOT required, because it is already present and would be green on arrival:
#   openai-compat          1     <- the kind is already named where reserved kinds are listed
$ErrorActionPreference = 'Continue'

$path = '.claude/skills/guardrails-domain-knowledge/SKILL.md'
if (-not (Test-Path $path)) {
    Write-Output "PRECONDITION: $path is missing - every clause below would crash."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw
$text = [regex]::Replace($raw, '(?s)<!--.*?-->', '')

$required = @{
    'PromptRole'  = 'the required role field that lets a runner refuse work it cannot honestly serve'
    'ServesRoles' = 'the build fact declaring which roles a kind serves (never a config key)'
    'tool_calls'  = 'the tool-capability probe and the accepts-tools-calls-none false green it closes'
    'MLX'         = 'the engine that made the protocol-vs-engine distinction load-bearing'
}

$failures = @()
foreach ($token in ($required.Keys | Sort-Object)) {
    if ($text -notmatch [regex]::Escape($token)) {
        $failures += "MISSING: '$token' - $($required[$token]). Baseline 0 on the starting tree."
    }
}

# The role gate is the single most consequential fact about v1, and a summary that omits it would leave a
# future agent believing a local model can take a task ACTION.
if ($text -notmatch 'Advisory') {
    $failures += "MISSING: 'Advisory' - v1 serves the Guardrail and Advisory roles ONLY and refuses an Action invocation. A summary that omits the role gate tells a future agent a local model can take a task action, which is the one thing this design refuses."
}

if ($failures.Count -gt 0) {
    Write-Output "=== domain-knowledge skill does not yet record the openai-compat runner ($($failures.Count) gap(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "Keep it proportionate - this is a knowledge skill, not a copy of the SSOT. Point at SSOT section 9.8 for the full contract. Do NOT reword the skill away from its own conventions to satisfy a pattern."
    exit 1
}

Write-Output "domain-knowledge skill records the openai-compat runner, its role gate and the tool-capability probe."
exit 0
