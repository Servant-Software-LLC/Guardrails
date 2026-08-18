# catches: wave 3 spending its whole retry budget building against wave-2 bytes that are not on this
#          branch. EVERY task in this wave reads the wave-1/2 resolver surface - TierResolver.Resolve
#          and SelectCandidate, the ServesTier candidacy predicate, the strength/specialization/costly
#          axes, TieringVerifierConfig.MinTier, and the Stage2PlanHarness/Stage2ConformanceTests pair
#          it EXTENDS - and the brief forbids re-deriving any of them. If the barrier delivered a tree
#          missing one, the task that needs it fails on a symbol its own writeScope EXCLUDES (earlier
#          waves own those files), so no retry can fix it and the failure is attributed to wave 3
#          instead of to the delivery.
#
# WAVE ENTRY GATE (SSOT 14.2): the #181 positive-baseline archetype at the wave boundary - the same
# boundary wave 2's exit gate certified, read from the other side. POSITIVE and assert-PRESENT ONLY:
# a wave-level preflight is evaluated against a segment that only ever grows, so a negative "not yet
# present" assertion would flip false the moment an unrelated file landed.
#
# Structural DECLARATION regexes, never bare name greps (dotnet.md 3): a name grep passes on the
# XML-doc paragraph that MENTIONS the member. Property checks key on the declaration up to the brace
# so they are accessor-order-insensitive (#112), and enum-member checks tolerate any trailing
# comma/brace so a correct enum cannot false-RED on member ORDER, which no rule constrains.
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
$runnerCfg  = 'src/Guardrails.Core/Model/PromptRunnerConfig.cs'
$tiering    = 'src/Guardrails.Core/Model/TieringConfig.cs'
$harness    = 'tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs'
$conform    = 'tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs'

# --- the resolver entry points the judge path extends ------------------------------------------
Require-Match $resolver 'public\s+static\s+TierResolution\s+SelectCandidate\s*\(' 'TierResolver.SelectCandidate - the 6.2 candidate selection the judge path reuses'
Require-Match $resolver 'public\s+static\s+TierResolution\s+Resolve\s*\('         'TierResolver.Resolve - the 6.1 precedence entry point whose result the judge rules key off'

# --- the ONE candidacy predicate the judge path must CALL, not re-implement (D22a) ---------------
Require-Match $runnerCfg 'ServesTier'  'PromptRunnerConfig.ServesTier - the single candidacy predicate. A second implementation in the judge path is the divergence D22a exists to forbid'
Require-Match $runnerCfg 'DeclaresTier' 'PromptRunnerConfig.DeclaresTier - the D28 excluded-only-for-cost half of the same pair'

# --- the axes the 6.5 rules decide on ------------------------------------------------------------
Require-Match $runnerCfg 'public\s+int\?\s+Strength\s*\{'   'PromptRunnerConfig.Strength - what rules 3/4 compare; without it "weak" has no declared form'
Require-Match $runnerCfg 'Specialization'                    'PromptRunnerConfig.Specialization - rule 6 breaks ties on it'
Require-Match $runnerCfg 'public\s+bool\?\s+Costly\s*\{'     'PromptRunnerConfig.Costly - the floor rule 5 and D29 turn on'

# --- the floor's config shape, which wave 3 is the FIRST consumer of ----------------------------
Require-Match $tiering 'public\s+TieringVerifierConfig\?\s+Verifier\s*\{' 'TieringConfig.Verifier - the block carrying the 6.5.1 floor'
Require-Match $tiering 'public\s+string\?\s+MinTier\s*\{'                 'TieringVerifierConfig.MinTier - the verifier FLOOR itself. It already exists and is UNREAD; wave 3 is its first consumer'

# --- the real-seam host and the suite wave 3 EXTENDS --------------------------------------------
Require-Match $harness 'class\s+Stage2PlanHarness\b'      'the Stage2PlanHarness real-seam host - wave 3 extends it rather than building a second, weaker host'
Require-Match $conform 'class\s+Stage2ConformanceTests\b' 'the Stage2ConformanceTests suite - wave 3 EXTENDS this exact class, and the plan terminal gate reads it by name'

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-3 entry gate: $($failures.Count) missing upstream artifact(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "waves 1-2 did not materialize on this branch. Wave 3 reads these members and CANNOT produce them (earlier waves own those files, so every wave-3 writeScope excludes them). Do not author around this: re-check that wave 2 completed and merged before wave 3 was scheduled."
    exit 1
}
Write-Output "wave-1/2 artifacts materialized: the resolver entry points, the ServesTier candidacy predicate, the strength/specialization/costly axes, the verifier floor's config shape, and the Stage2 harness + conformance suite."
exit 0
