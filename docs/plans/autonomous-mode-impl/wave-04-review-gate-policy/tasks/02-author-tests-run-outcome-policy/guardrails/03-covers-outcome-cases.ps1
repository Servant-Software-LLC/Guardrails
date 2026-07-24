# catches: a RunOutcomePolicyTests file that encodes only the easy case (best-guess ⇒ suppress) and
#          silently drops the load-bearing ones — the NO-machine-decision negative case (a plain run
#          delivers normally), the proceeded-unreviewed suppression, or the ProceededUnreviewedWaveCount
#          count (the 'ran with N unreviewed waves' flag). A LOWER BOUND on the authored test file (a
#          covers-key-behaviors floor): it forces each outcome case to be named; whether the asserts are
#          CORRECT stays for the implementation task's tests-pass + /guardrails-review. Scoped to the one
#          test file this task authors.
$test = "tests/Guardrails.Core.Tests/RunOutcomePolicyTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the run-outcome-policy tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
$required = @('ProceededBestGuess', 'ProceededUnreviewed', 'SuppressesDelivery', 'ProceededUnreviewedWaveCount')
$missing = @()
foreach ($token in $required) {
    if ($c -notmatch [regex]::Escape($token)) { $missing += $token }
}
if ($missing.Count -gt 0) {
    Write-Output ("RunOutcomePolicyTests does not exercise: " + ($missing -join ', ') + " — each load-bearing outcome (best-guess/unreviewed suppression + the unreviewed-wave count) must be tested")
    exit 1
}
# The load-bearing NEGATIVE case: a run with NO machine decision must NOT suppress delivery.
if ($c -notmatch '(?i)\bfalse\b') {
    Write-Output "RunOutcomePolicyTests never asserts a FALSE outcome — the negative case (no machine decision ⇒ SuppressesDelivery is false ⇒ a plain run still delivers) is missing"
    exit 1
}
exit 0
