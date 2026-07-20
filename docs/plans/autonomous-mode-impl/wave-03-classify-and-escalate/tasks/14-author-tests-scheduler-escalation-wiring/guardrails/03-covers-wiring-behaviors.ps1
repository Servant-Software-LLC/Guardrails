# catches: a SchedulerEscalationWiringTests file that proves only escalation and drops the other wired
#          behaviours — proceeded-best-guess (below threshold), blocker-retried (class-b), answer-injected
#          (resume), or the distinct escalations-pending exit code. A LOWER BOUND on the wiring test (a
#          covers-key-behaviors floor): it forces all five wired behaviours to be NAMED so the #120
#          composition-root test cannot silently verify one path; whether the asserts are CORRECT stays
#          for task 15's tests-pass + the pre-merge human review (#375). Scoped to the one wiring test file.
$test = "tests/Guardrails.Integration.Tests/SchedulerEscalationWiringTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the scheduler-escalation wiring tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
$missing = @()
foreach ($token in @('escalated', 'proceeded-best-guess', 'blocker-retried', 'answer-injected')) {
    if ($c -notmatch [regex]::Escape($token)) { $missing += $token }
}
if ($c -notmatch '(?i)EscalationsPending') { $missing += 'EscalationsPending-exit-code' }
if ($missing.Count -gt 0) {
    Write-Output ("SchedulerEscalationWiringTests must prove all five wired behaviours reachable from the REAL SchedulerFactory.Create — missing: " + ($missing -join ', '))
    exit 1
}
exit 0
