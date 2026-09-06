# catches: the second deliverable silently skipped - the new GR2037 entries landing while the existing
#          test-suite fixtures they red are left exposed. Those three files belong to no OTHER task, so the
#          breakage would surface at the TERMINAL GATE, where nothing can fix it (the #587 tripwire shape).
#          Forbidden-present, so it is SUPPOSED to be red before this task and green after (#478 exempts
#          this polarity from the measured-zero rule).
# Measured baseline (2026-09-06, master a3f3e977): exactly 3 files carry a guardrail fixture setting
#          ErrorActionPreference = 'Continue', and that is the ONLY count this script checks. #608b has
#          ZERO collateral - an earlier draft claimed two integration-test files, but those bodies are
#          stub AGENT RUNNERS (they read stdin and emit {"type":"result"}), which the validator never
#          scans; that clause was DELETED rather than kept green-on-arrival, and this comment no longer
#          declares a second count it does not measure. If your grep finds a different set, trust it.
$ErrorActionPreference = 'Stop'

$problems = New-Object System.Collections.Generic.List[string]

$continueFiles = @(
    'tests/Guardrails.Core.Tests/GuardrailRequiresForbiddenTokenTests.cs',
    'tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs',
    'tests/Guardrails.Core.Tests/ProducerCoverageTests.cs'
)
foreach ($f in $continueFiles) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) {
        $problems.Add("[$f] does not exist - this guardrail names the file explicitly, so a rename needs reconciling here too.")
        continue
    }
    $raw = Get-Content -Raw -LiteralPath $f
    $hits = @([regex]::Matches($raw, "ErrorActionPreference\s*=\s*'Continue'")).Count
    if ($hits -gt 0) {
        $problems.Add("[$f] still carries $hits guardrail fixture(s) setting ErrorActionPreference = 'Continue'. Registry entry #608a reds them, and no task owns this file except this one - left as is, the whole plan red-halts at the terminal gate with nothing able to fix it. Change the fixture to 'Stop' (it does not alter what the fixture tests).")
    }
}

if ($problems.Count -gt 0) {
    Write-Output "=== Existing fixtures still exposed to the new entries ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "All three previously-exposed test files are immunized: no guardrail fixture sets ErrorActionPreference = 'Continue'."
exit 0
