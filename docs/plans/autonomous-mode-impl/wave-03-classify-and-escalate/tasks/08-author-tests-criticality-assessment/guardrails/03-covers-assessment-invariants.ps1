# catches: a CriticalityAssessmentTests file that tests only the happy threshold compare and silently
#          drops the load-bearing safety invariants — malformed-assessment-⇒-escalate (the judge is never
#          the verdict authority, §4.3), the proceed-unreviewed clamp (high/critical always escalate,
#          §5.2), or the maxJudgeWidenings run-level cap. A LOWER BOUND on the authored test file (a
#          covers-key-behaviors floor): it forces each safety invariant to be named; whether the asserts
#          are CORRECT stays for the implementation task's tests-pass + /guardrails-review. Scoped to the
#          one test file this task authors.
$test = "tests/Guardrails.Core.Tests/CriticalityAssessmentTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the criticality-assessment tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
$required = @('proceed-unreviewed', 'maxJudgeWidenings', 'escalationThreshold')
$missing = @()
foreach ($token in $required) {
    if ($c -notmatch [regex]::Escape($token)) { $missing += $token }
}
# The malformed/absent-⇒-escalate invariant must be present (accept any of these spellings).
if ($c -notmatch '(?i)malformed|unparseable|empty|invalid') { $missing += 'malformed/absent-assessment' }
if ($missing.Count -gt 0) {
    Write-Output ("CriticalityAssessmentTests does not cover: " + ($missing -join ', ') + " — the assessment safety invariants (malformed⇒escalate, the proceed-unreviewed clamp, the maxJudgeWidenings cap) must each be tested")
    exit 1
}
exit 0
