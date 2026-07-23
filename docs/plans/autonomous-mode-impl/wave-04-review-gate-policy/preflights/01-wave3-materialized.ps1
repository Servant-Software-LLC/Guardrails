# catches: wave-04 (review-gate policy + proceed-unreviewed + overwatcher auto-tier) starting before
#          waves 1-3's outputs are materialized on the branch. The #181 POSITIVE-baseline archetype at the
#          wave boundary (SSOT §14.3): terminal-gate-of-wave-3 == preflight-of-wave-4. Positive-monotone-
#          safe (assert-PRESENT, never "not yet present" — a branch only grows). Confirms wave-04 builds on
#          the REAL Wave-3 decision tokens (proceeded-best-guess / proceeded-unreviewed), the Wave-3 exit
#          scheme (EscalationsPending), the Wave-2 review-gate threshold (ReviewGateDecision), the shipped
#          escalation sink + clamp, the mergeOnSuccess/autonomy config surface, and the review marker —
#          not on possibly-absent bytes.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

function Assert-Contains($rel, $needle, $why) {
    $path = Join-Path $ws $rel
    if (-not (Test-Path $path)) {
        Write-Output "$rel not materialized — $why"
        exit 1
    }
    if ((Get-Content -Raw -Path $path) -notmatch $needle) {
        Write-Output "$rel has no '$needle' — $why"
        exit 1
    }
}

# 1. Wave-3 decision tokens the mergeOnSuccess-OFF / distinct-exit / N-unreviewed-flag all read.
Assert-Contains 'src/Guardrails.Core/Execution/DecisionEntry.cs' 'ProceededBestGuess' `
    "wave-03's proceeded-best-guess decision token is not materialized; wave-04's run-outcome policy has nothing to key off"
Assert-Contains 'src/Guardrails.Core/Execution/DecisionEntry.cs' 'ProceededUnreviewed' `
    "wave-03's proceeded-unreviewed decision token is not materialized; wave-04's review-gate opt-in cannot record it"

# 2. Wave-3 exit-code scheme this wave extends with ProceededUnreviewed = 5 (the next free value after 4).
Assert-Contains 'src/Guardrails.Cli/ExitCodes.cs' 'EscalationsPending' `
    "wave-03's ExitCodes.EscalationsPending (=4) is not materialized; wave-04's distinct exit code has no scheme to extend"

# 3. Wave-2 review-gate threshold + the compound-config predicate wave-04's review-gate policy is consistent with.
Assert-Contains 'src/Guardrails.Core/Model/AutonomyConfig.cs' 'ReviewGateDecision' `
    "wave-02's ReviewGateDecision (escalate / proceed-unreviewed) is not materialized; wave-04 has no review-gate threshold to consult"
Assert-Contains 'src/Guardrails.Core/Loading/PlanValidator.cs' 'ViolatesCompoundConfig' `
    "wave-02's GR2040 ViolatesCompoundConfig predicate is not materialized; wave-04's review-gate policy has nothing to stay consistent with"

# 4. Wave-3 escalation sink + clamp the Option-E escalate path reuses.
Assert-Contains 'src/Guardrails.Core/Execution/FileEscalationSink.cs' 'class\s+FileEscalationSink' `
    "wave-03's file escalation sink is not materialized; wave-04's review-gate Option-E escalate has no sink to reuse"
Assert-Contains 'src/Guardrails.Core/Execution/CriticalityJudge.cs' 'ReviewGate' `
    "wave-03's CriticalityJudge proceed-unreviewed clamp is not materialized; wave-04's review-gate policy must stay consistent with it"

# 5. Config surface the mergeOnSuccess-OFF + overwatcher-auto-tier-gating build on.
Assert-Contains 'src/Guardrails.Core/Model/RunConfig.cs' 'MergeOnSuccess' `
    "RunConfig.MergeOnSuccess is not materialized; wave-04 cannot default it OFF on a best-guess run"
Assert-Contains 'src/Guardrails.Core/Model/RunConfig.cs' '\bAutonomy\b' `
    "RunConfig.Autonomy (the autonomy block) is not materialized; wave-04's overwatcher auto-tier gates on its PRESENCE"

# 6. The overwatcher + review marker wave-04 extends / references for the no-forged-marker invariant.
Assert-Contains 'src/Guardrails.Core/Execution/Overwatch.cs' 'class\s+Overwatch' `
    "the Overwatch actor is not materialized; wave-04's auto-tier gating has nothing to extend"
Assert-Contains 'src/Guardrails.Core/Review/ReviewMarker.cs' '(class|record)\s+ReviewMarker' `
    "ReviewMarker is not materialized; wave-04's 'harness never forges a review marker' invariant has nothing to assert against"

exit 0
