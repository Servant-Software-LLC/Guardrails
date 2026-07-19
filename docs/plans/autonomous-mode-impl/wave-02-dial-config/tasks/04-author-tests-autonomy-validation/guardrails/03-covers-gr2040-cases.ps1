# catches: an AutonomyValidatorTests file that encodes only ONE GR2040 case (e.g. just the run-wide
#          dial:critical) and silently drops the per-gate route-around (needs-human:critical +
#          proceed-unreviewed) — the load-bearing Finding-3 case. A LOWER BOUND on the authored test
#          file (a covers-key-behaviors floor): it forces BOTH GR2040 cases to be present; whether the
#          asserts are CORRECT stays for the implementation task's tests-pass + /guardrails-review.
#          Scoped to the one test file this task authors.
$test = "tests/Guardrails.Core.Tests/AutonomyValidatorTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the autonomy-validation tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
if (([regex]::Matches($c, 'GR2040')).Count -lt 2 -or $c -notmatch 'needs-human' -or $c -notmatch 'proceed-unreviewed') {
    Write-Output "AutonomyValidatorTests must cover BOTH the run-wide (dial:critical) AND per-gate (needs-human:critical + proceed-unreviewed) GR2040 route-around cases"
    exit 1
}
exit 0
