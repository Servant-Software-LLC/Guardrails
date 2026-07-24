# catches: the §2/§2.1 autonomy-block delta claimed done but the config contract was not actually
#          written into docs/plans/02-schemas-and-contracts.md — the harness config work would then land
#          without its SSOT-first contract (invariant 4). Greps for tokens the DELTA adds
#          (escalationThreshold / gateThresholds / blockerRetry / maxJudgeWidenings / GR2039 / GR2040) —
#          NOT the bare word 'autonomy' (autonomyPolicy already exists in the doc). Scoped to the one file.
#          LOWER BOUND: it also pins `byte-identical` (the inert-by-default back-compat statement) and
#          `maxCostUsd` (the --autonomous-requires-maxCostUsd rule) so those load-bearing sentences cannot
#          go unverified; whether the surrounding prose is CORRECT stays a human-judgment residual for
#          /guardrails-review.
$doc = "docs/plans/02-schemas-and-contracts.md"
if (-not (Test-Path $doc)) {
    Write-Output "$doc does not exist"
    exit 1
}
$content = Get-Content -Raw -Path $doc
$required = @('escalationThreshold', 'gateThresholds', 'blockerRetry', 'maxJudgeWidenings', 'GR2039', 'GR2040', 'byte-identical', 'maxCostUsd')
foreach ($token in $required) {
    if ($content -notmatch [regex]::Escape($token)) {
        Write-Output "$doc does not document '$token' — the #361 autonomy-block config contract (doc 12 §11) is not in the SSOT"
        exit 1
    }
}
exit 0
