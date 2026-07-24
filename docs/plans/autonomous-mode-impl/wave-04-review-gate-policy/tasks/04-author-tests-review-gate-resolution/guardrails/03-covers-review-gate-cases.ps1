# catches: a SchedulerReviewGateTests file that encodes only the easy path and drops a load-bearing one —
#          the proceed-unreviewed RUN + recorded proceeded-unreviewed decision, the default escalate HALT
#          (Option E), or the never-forged review-marker floor. A LOWER BOUND (covers-key-behaviors floor):
#          it forces each resolution branch + the marker-absence assertion to be named; whether the asserts
#          are CORRECT stays for task 05's tests-pass + /guardrails-review. Scoped to the one test file.
$test = "tests/Guardrails.Core.Tests/SchedulerReviewGateTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the review-gate-resolution tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
$required = @('ProceedUnreviewed', 'ProceededUnreviewed', 'review-gate', 'ReviewMarker')
$missing = @()
foreach ($token in $required) {
    if ($c -notmatch [regex]::Escape($token)) { $missing += $token }
}
if ($missing.Count -gt 0) {
    Write-Output ("SchedulerReviewGateTests does not exercise: " + ($missing -join ', ') + " — the proceed-unreviewed RUN, the recorded proceeded-unreviewed decision, the escalate HALT, and the never-forged review-marker floor must each be tested")
    exit 1
}
exit 0
