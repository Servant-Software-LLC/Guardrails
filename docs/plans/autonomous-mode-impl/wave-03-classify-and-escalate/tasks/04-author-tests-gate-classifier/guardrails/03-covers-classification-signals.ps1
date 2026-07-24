# catches: a GateClassifierTests file that encodes only the easy cases (Transient, needsHuman) and
#          silently drops the load-bearing ones — the UNKNOWN-defaults-to-escalate safe default (§4.3),
#          the terminal-exhaustion FLOOR (never best-guessed past, invariant 5), or the permission-wall /
#          RunAbort / preflight class-(c) signals. A LOWER BOUND on the authored test file (a
#          covers-key-behaviors floor): it forces every classification signal to be named; whether the
#          asserts are CORRECT stays for the implementation task's tests-pass + /guardrails-review.
#          Scoped to the one test file this task authors.
$test = "tests/Guardrails.Core.Tests/GateClassifierTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the gate-classifier tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
# Each token is a distinct classification signal that MUST be exercised (case-insensitive).
$required = @('Transient', 'PermissionWall', 'RunAbort', 'preflight', 'needsHuman', 'wave-checkpoint', 'TerminalExhaustion', 'unknown')
$missing = @()
foreach ($token in $required) {
    if ($c -notmatch [regex]::Escape($token)) { $missing += $token }
}
if ($missing.Count -gt 0) {
    Write-Output ("GateClassifierTests does not exercise these signals: " + ($missing -join ', ') + " — the classifier's load-bearing cases (esp. unknown-defaults-to-class-c and the terminal-exhaustion floor) must each be tested")
    exit 1
}
exit 0
