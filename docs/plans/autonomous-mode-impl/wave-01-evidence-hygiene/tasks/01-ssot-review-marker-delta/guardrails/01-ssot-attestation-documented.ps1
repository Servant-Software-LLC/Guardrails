# catches: the SSOT §13 delta claimed done but the attestation contract was not actually written into
#          docs/plans/02-schemas-and-contracts.md — the harness work would then land without its
#          SSOT-first contract (invariant 4). Scoped to the one file this task owns. This is a LOWER
#          BOUND: it pins the load-bearing distinctive tokens (incl. `write-access` for the trust-boundary
#          rewrite that DROPS the "unforgeable" framing, and `per-wave` for the multi-wave note) so those
#          rewrites cannot go unverified; whether the surrounding prose is CORRECT stays a human-judgment
#          residual for /guardrails-review.
$doc = "docs/plans/02-schemas-and-contracts.md"
if (-not (Test-Path $doc)) {
    Write-Output "$doc does not exist"
    exit 1
}
$content = Get-Content -Raw -Path $doc
$required = @('attestation', 'review-artifact', 'bare', 'machine', 'reportDigest', 'Plan-Definition-Hash', 'write-access', 'per-wave')
foreach ($token in $required) {
    if ($content -notmatch [regex]::Escape($token)) {
        Write-Output "$doc does not document '$token' — the #366 attestation contract (doc 16 §12.1) is not in the SSOT"
        exit 1
    }
}
if ($content -notmatch '(?i)audit') {
    Write-Output "$doc does not state the marker is read for AUDIT (not by the Scheduler) — the #366 no-runtime-gate framing is missing"
    exit 1
}
exit 0
