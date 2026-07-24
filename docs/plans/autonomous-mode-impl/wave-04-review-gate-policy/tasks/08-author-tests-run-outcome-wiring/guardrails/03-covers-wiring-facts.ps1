# catches: a RunOutcomeWiringTests file that encodes only one fact and drops the load-bearing ones — the
#          delivery-suppression (WhollyGreenButUndelivered), the proceed-unreviewed scenario, the
#          'ran with N unreviewed waves' flag, or the never-forged review-marker floor. A LOWER BOUND
#          (covers-key-behaviors floor): it forces each of the four observable facts to be named; whether
#          the asserts are CORRECT stays for the sink task's tests-pass + /guardrails-review. Scoped to the
#          one test file this task authors.
$test = "tests/Guardrails.Integration.Tests/RunOutcomeWiringTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the run-outcome-wiring tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
$required = @('WhollyGreenButUndelivered', 'proceed-unreviewed', 'unreviewed', 'guardrails-review')
$missing = @()
foreach ($token in $required) {
    if ($c -notmatch [regex]::Escape($token)) { $missing += $token }
}
if ($missing.Count -gt 0) {
    Write-Output ("RunOutcomeWiringTests does not exercise: " + ($missing -join ', ') + " — delivery suppression, the proceed-unreviewed scenario, the N-unreviewed-waves flag, and the never-forged review-marker floor must each be tested")
    exit 1
}
exit 0
