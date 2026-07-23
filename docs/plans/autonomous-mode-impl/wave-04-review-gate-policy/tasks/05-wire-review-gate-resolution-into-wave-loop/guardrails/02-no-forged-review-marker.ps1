# catches: the security-sensitive floor breach (§5 floor 3, #375/#366) — the review-gate resolution
#          FORGING an attestation by writing the review marker on a human's behalf. A tempting shortcut for
#          the Option-P proceed path is to stamp state/guardrails-review.json so the wave reads as
#          reviewed; that is exactly what the harness must NEVER do. Negative assertion (fail-on-present,
#          archetype #11), scoped to the one file this task owns: Scheduler.cs must NOT call the marker
#          writer or write the marker file. GR2026 stays (correctly) silent on a require-ABSENT keyword.
$scheduler = "src/Guardrails.Core/Execution/Scheduler.cs"
if (-not (Test-Path $scheduler)) {
    Write-Output "$scheduler does not exist"
    exit 1
}
$sc = Get-Content -Raw -Path $scheduler
# Strip C# comments so a "// never write guardrails-review.json" note does not false-fire (comment-blind, #97).
$code = [regex]::Replace($sc, '/\*[\s\S]*?\*/', ' ')
$code = [regex]::Replace($code, '//[^\r\n]*', ' ')
if ($code -match 'ReviewMarker\s*\.\s*Write' -or $code -match 'WriteMarker' -or $code -match 'guardrails-review\.json') {
    Write-Output "Scheduler.cs writes the review marker (ReviewMarker.Write / WriteMarker / guardrails-review.json) in CODE — the harness must NEVER forge a review attestation on a human's behalf (§5 floor 3, #375/#366). The Option-P path records a proceeded-unreviewed decision; it does NOT mark the wave reviewed."
    exit 1
}
exit 0
