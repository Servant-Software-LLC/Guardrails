# catches: the §2.1/§5.3/§7.1 Phase-4 review-gate-policy delta claimed done but the contract was not
#          actually written into docs/plans/02-schemas-and-contracts.md — the harness review-gate /
#          run-outcome / overwatcher-auto-tier work would then land without its SSOT-first contract
#          (invariant 4). Requires the THREE Phase-4-distinctive co-occurrences (windowed, both orders) so
#          it cannot false-pass on wave-01/02/03 content that already mentions the bare tokens:
#          (1) mergeOnSuccess paired with a recorded proceeded- machine decision (the #340 delivery gate);
#          (2) the 'autonomy' block presence paired with the overwatcher/auto-tier gate (anti-Option-(c));
#          (3) proceeded-unreviewed paired with a distinct exit. Scoped to the one file. LOWER BOUND — it
#          pins the load-bearing NEW sentences; whether the surrounding prose is CORRECT stays a
#          human-judgment residual for /guardrails-review.
$doc = "docs/plans/02-schemas-and-contracts.md"
if (-not (Test-Path $doc)) {
    Write-Output "$doc does not exist"
    exit 1
}
$content = Get-Content -Raw -Path $doc

# (1) delivery gate: mergeOnSuccess OFF when a proceeded- machine decision was recorded (#340 / §5.3).
if ($content -notmatch 'mergeOnSuccess[\s\S]{0,400}proceeded-(best-guess|unreviewed)' -and
    $content -notmatch 'proceeded-(best-guess|unreviewed)[\s\S]{0,400}mergeOnSuccess') {
    Write-Output "$doc does not document mergeOnSuccess defaulting OFF on a recorded proceeded-best-guess/proceeded-unreviewed decision (the #340 delivery reconciliation, doc 12 §1/§5.3) — required Phase-4 contract"
    exit 1
}

# (2) overwatcher auto-tier gated on the PRESENCE of the autonomy block (anti-Option-(c), §2.1/§9.2).
if ($content -notmatch '(presence|present)[\s\S]{0,160}autonomy[\s\S]{0,40}block' -and
    $content -notmatch 'autonomy[\s\S]{0,40}block[\s\S]{0,160}(presence|present)') {
    Write-Output "$doc does not document the overwatcher auto-tier silent auto-apply being gated on the PRESENCE of the autonomy block (anti-Option-(c) back-compat, doc 12 §9 Phase 4) — required Phase-4 contract"
    exit 1
}

# (3) distinct exit code for a proceeded-unreviewed run (§7.1).
if ($content -notmatch 'proceeded-unreviewed[\s\S]{0,300}(exit|ProceededUnreviewed)' -and
    $content -notmatch '(exit|ProceededUnreviewed)[\s\S]{0,300}proceeded-unreviewed') {
    Write-Output "$doc does not document the distinct non-zero exit code for a proceeded-unreviewed run (ProceededUnreviewed, doc 12 §7.1) — required Phase-4 contract"
    exit 1
}
exit 0
