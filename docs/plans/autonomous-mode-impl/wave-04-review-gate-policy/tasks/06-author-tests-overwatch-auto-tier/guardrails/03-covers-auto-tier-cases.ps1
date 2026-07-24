# catches: an OverwatchAutoTierTests file that encodes only the happy auto-apply case and drops a
#          load-bearing one — the byte-identical back-compat (auto + NO block ⇒ still prompts, the
#          anti-Option-(c) guard) or the Denylist-never-auto-applies floor. A LOWER BOUND
#          (covers-key-behaviors floor): it forces the gate knob (autonomyBlockPresent), the policy (Auto),
#          the prompt/auto discriminator (ConfirmApply), and the Denylist floor to be named; whether the
#          asserts are CORRECT stays for task 07's tests-pass + /guardrails-review. Scoped to the one file.
$test = "tests/Guardrails.Core.Tests/OverwatchAutoTierTests.cs"
if (-not (Test-Path $test)) {
    Write-Output "$test does not exist — the overwatch auto-tier tests were not authored"
    exit 1
}
$c = Get-Content -Raw -Path $test
$required = @('autonomyBlockPresent', 'Auto', 'ConfirmApply', 'Denylist')
$missing = @()
foreach ($token in $required) {
    if ($c -notmatch [regex]::Escape($token)) { $missing += $token }
}
if ($missing.Count -gt 0) {
    Write-Output ("OverwatchAutoTierTests does not exercise: " + ($missing -join ', ') + " — the block-present auto-apply, the auto+NO-block back-compat (ConfirmApply still called), and the Denylist-never-auto-applies floor must each be tested")
    exit 1
}
exit 0
