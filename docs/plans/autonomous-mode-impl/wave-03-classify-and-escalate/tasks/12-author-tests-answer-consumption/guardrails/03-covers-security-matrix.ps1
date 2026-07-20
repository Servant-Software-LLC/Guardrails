# catches: an AnswerFileConsumptionTests file that covers the happy path but silently drops a security
#          case — the exact holes DA-7 warns about. This is a SECURITY LOWER BOUND (covers-key-behaviors
#          floor) with teeth: every attack/defence in doc 12 §7.6 must be NAMED as a test. Whether the
#          asserts are CORRECT stays for the implementation task's tests-pass + the hard /guardrails-review
#          this security-sensitive wave gets. Scoped to the one test file this task authors.
$test = "tests/Guardrails.Core.Tests/AnswerFileConsumptionTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the answer-consumption security tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
# Each token is a distinct security case that MUST appear (case-sensitive where it is a wire token).
$required = @(
    'answer-injected',       # valid ⇒ injected + status flip
    'definitionHash',        # dual-hash staleness binding
    'consumed',              # once-only status
    'review-gate',           # no review-attested kind — rejected
    'proceed-unreviewed',    # clamped hard call non-answerable
    'terminal'               # terminal escalation not answerable
)
$missing = @()
foreach ($token in $required) {
    if ($c -notmatch [regex]::Escape($token)) { $missing += $token }
}
# seq-replay defence and the wrong-identity/binding rejection must each be present (accept spellings).
if ($c -notmatch '(?i)seq') { $missing += 'seq-replay' }
if ($c -notmatch '(?i)wrong.identity|binding|mismatch') { $missing += 'wrong-identity-binding' }
if ($c -notmatch '(?i)stale') { $missing += 'stale-hash' }
if ($missing.Count -gt 0) {
    Write-Output ("AnswerFileConsumptionTests is missing security cases: " + ($missing -join ', ') + " — every DA-7 attack/defence (binding, stale-replay, seq-uniqueness, CAS once-only, review-gate-reject, clamped-hard-call-reject, terminal-not-answerable) must be a named test")
    exit 1
}
exit 0
