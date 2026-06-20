# catches: a vacuous MergeLockAndSettleTests that compiles-fails (passing tests-fail-on-current-code
#          trivially) but never encodes the load-bearing scenarios - FF-is-free + trailer, and the
#          non-FF union B1 four-effect rollback (reset --hard preHead, no fragment, mergeSequence not
#          consumed, needs-human). Scoped to the one file this task owns.
$file = "tests/Guardrails.Integration.Tests/MergeLockAndSettleTests.cs"
$text = Get-Content $file -Raw
$needles = @('ff-only', 'preHead', 'trailer')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch [regex]::Escape($n)) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "MergeLockAndSettleTests is missing the load-bearing settle scenario term(s) [$($missing -join ', ')] - FF-is-free / B1 four-effect rollback not encoded"
    exit 1
}
exit 0
