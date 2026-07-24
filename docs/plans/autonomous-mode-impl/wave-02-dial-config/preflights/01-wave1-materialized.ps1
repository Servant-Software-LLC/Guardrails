# catches: wave-02 (the dial config surface) starting before wave-01's evidence-hygiene outputs are
#          materialized on the branch. This is the #181 POSITIVE-baseline archetype at the wave boundary
#          (SSOT §14.3): terminal-gate-of-wave-1 == preflight-of-wave-2. Positive-monotone-safe
#          (assert-PRESENT, never "not yet present" — a branch only grows). Confirms wave-02 builds on
#          real bytes, not possibly-absent ones.
$files = @(
    'src/Guardrails.Core/Review/ReviewAttestation.cs',
    'src/Guardrails.Cli/Commands/PlanHashCommand.cs'
)
foreach ($rel in $files) {
    if (-not (Test-Path $rel)) {
        Write-Output "$rel not materialized on the branch — wave-01's evidence-hygiene output is missing; wave-02 cannot build on it"
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace((Get-Content -Raw -Path $rel))) {
        Write-Output "$rel is present but empty — wave-01 did not materialize real content"
        exit 1
    }
}
# The attestation field + the mark-reviewed --evidence flow are what wave-02's SSOT/config work assumes exist.
if ((Get-Content -Raw -Path 'src/Guardrails.Core/Review/ReviewMarker.cs') -notmatch '(?i)attestation') {
    Write-Output "src/Guardrails.Core/Review/ReviewMarker.cs has no attestation member — wave-01's #366 marker change is not materialized"
    exit 1
}
if ((Get-Content -Raw -Path 'src/Guardrails.Cli/Commands/MarkReviewedCommand.cs') -notmatch '--evidence|evidence') {
    Write-Output "src/Guardrails.Cli/Commands/MarkReviewedCommand.cs has no --evidence support — wave-01's F2 change is not materialized"
    exit 1
}
exit 0
