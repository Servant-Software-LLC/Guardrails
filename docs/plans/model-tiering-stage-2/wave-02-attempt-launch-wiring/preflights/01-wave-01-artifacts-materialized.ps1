# catches: wave 2 spending its whole retry budget building against wave-1 bytes that are not on this
#          branch. EVERY task in this wave READS the wave-1 resolver surface - TierResolver.Resolve /
#          SelectCandidate, the TierResolution datums (NoRoute / Climbed / CostlyCeilingBound /
#          CostlyCeilingBlocks) and ActionDefinition.TierOrigin - and the brief forbids re-deriving
#          any of them. If the barrier delivered a tree missing one, the task that needs it fails on
#          a symbol its own writeScope EXCLUDES (wave 1 owns those files), so no retry can fix it and
#          the failure is attributed to wave 2 instead of to the delivery.
#
# WAVE ENTRY GATE (SSOT 14.2): the #181 positive-baseline archetype at the wave boundary - the same
# boundary wave 1's exit gate certified, read from the other side. POSITIVE and assert-PRESENT ONLY:
# a wave-level preflight is evaluated against a segment that only ever grows, so a negative "not yet
# present" assertion would flip false the moment an unrelated file landed.
#
# Structural DECLARATION regexes, never bare name greps (dotnet.md 3): a name grep passes on the
# XML-doc paragraph that MENTIONS the member. Property checks key on the declaration up to the brace
# so they are accessor-order-insensitive (#112).
$ErrorActionPreference = 'Continue'
$failures = @()

function Require-Match {
    param([string]$File, [string]$Pattern, [string]$What)
    if (-not (Test-Path $File)) {
        $script:failures += "$File does not exist - $What"
        return
    }
    $content = Get-Content -Raw $File
    if ($content -notmatch $Pattern) {
        $script:failures += "$File does not declare $What (pattern: $Pattern)"
    }
}

$resolver   = 'src/Guardrails.Core/Prompts/TierResolver.cs'
$resolution = 'src/Guardrails.Core/Prompts/TierResolution.cs'
$action     = 'src/Guardrails.Core/Model/ActionDefinition.cs'

# --- 6.2 selection + 6.1 precedence: the two entry points the attempt launcher calls ---------------
Require-Match $resolver 'public\s+static\s+TierResolution\s+SelectCandidate\s*\(' 'TierResolver.SelectCandidate - the 6.2 candidate-selection entry point'
Require-Match $resolver 'public\s+static\s+TierResolution\s+Resolve\s*\('         'TierResolver.Resolve - the 6.1 precedence entry point wave 2 wires in'

# --- the TierResolution datums wave 2 READS (and must not re-derive) -------------------------------
Require-Match $resolution 'public\s+bool\s+NoRoute\s*\{'                              'TierResolution.NoRoute - the 6.2 no-candidate outcome task 08 settles on'
Require-Match $resolution 'public\s+bool\s+Climbed\s*\{'                              'TierResolution.Climbed - the climb datum task 07/09 record and log'
Require-Match $resolution 'public\s+bool\s+CostlyCeilingBound\s*\{'                   'TierResolution.CostlyCeilingBound - the D28 binding-ceiling datum task 09 warns on'
Require-Match $resolution 'public\s+IReadOnlyList<string>\s+CostlyCeilingBlocks\s*\{' 'TierResolution.CostlyCeilingBlocks - the block NAMES the D28 warning prints'
Require-Match $resolution 'public\s+string\?\s+RequestedTier\s*\{'                    'TierResolution.RequestedTier - the rung that was asked for'
Require-Match $resolution 'public\s+string\?\s+Tier\s*\{'                             'TierResolution.Tier - the rung actually served'
Require-Match $resolution 'public\s+bool\s+Pinned\s*\{'                               'TierResolution.Pinned - the D31 tierSource=override input'
Require-Match $resolution 'public\s+bool\s+Legacy\s*\{'                               'TierResolution.Legacy - the D30 no-rung path, which records NO tierSource'

# --- the loader-restored provenance input (D31 table) ----------------------------------------------
Require-Match $action 'public\s+TierOrigin\s+TierOrigin\s*\{' 'ActionDefinition.TierOrigin - the task-vs-plan-default origin wave 2 maps to journal tierSource. Wave 2 MUST read this; re-deriving it by comparing Tier to tiering.defaultTier is the shipped PlanValidator workaround and is wrong exactly when a task tier equals the default'
Require-Match $action '(?m)^\s*PlanDefault\s*$'               'the TierOrigin.PlanDefault member (journal tierSource "plan-default")'
Require-Match $action '(?m)^\s*Task,\s*$'                     'the TierOrigin.Task member (journal tierSource "task")'

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-2 entry gate: $($failures.Count) missing wave-1 artifact(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "wave 1's outputs did not materialize on this branch. Wave 2 reads these members and CANNOT produce them (wave 1 owns those files, so every wave-2 writeScope excludes them). Do not author around this: re-check that wave 1 completed and merged before wave 2 was scheduled."
    exit 1
}
Write-Output "wave-1 artifacts materialized: TierResolver (Resolve + SelectCandidate), every TierResolution datum wave 2 reads, and ActionDefinition.TierOrigin."
exit 0
