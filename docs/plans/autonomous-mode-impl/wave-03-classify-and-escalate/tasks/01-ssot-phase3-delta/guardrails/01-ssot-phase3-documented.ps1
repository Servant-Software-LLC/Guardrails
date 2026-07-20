# catches: the §7/§8 Phase-3 forensic/escalation delta claimed done but the contract was not actually
#          written into docs/plans/02-schemas-and-contracts.md — the harness escalation/answer work
#          would then land without its SSOT-first contract (invariant 4). Greps for tokens the DELTA
#          adds (autonomy.jsonl / escalations / answer-injected / proceeded-best-guess / blocker-retried /
#          escalated / definitionHash) — NOT bare 'decision' or 'escalate' (which may already appear).
#          Scoped to the one file. LOWER BOUND: it pins the load-bearing NEW tokens/paths so those
#          sentences cannot go unverified; whether the surrounding prose is CORRECT stays a
#          human-judgment residual for /guardrails-review.
$doc = "docs/plans/02-schemas-and-contracts.md"
if (-not (Test-Path $doc)) {
    Write-Output "$doc does not exist"
    exit 1
}
$content = Get-Content -Raw -Path $doc
$required = @(
    'autonomy.jsonl',
    'escalations',
    'answer-injected',
    'proceeded-best-guess',
    'blocker-retried',
    'escalated',
    'definitionHash',
    'answer.json'
)
foreach ($token in $required) {
    if ($content -notmatch [regex]::Escape($token)) {
        Write-Output "$doc does not document '$token' — the #361 Phase-3 forensic/escalation contract (doc 12 §6.2/§6.3/§7/§8) is not in the SSOT"
        exit 1
    }
}
exit 0
