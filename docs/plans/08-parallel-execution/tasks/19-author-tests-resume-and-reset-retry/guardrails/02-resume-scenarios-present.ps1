# catches: a vacuous ResumeAndResetRetryTests that compiles-fails (passing tests-fail-on-current-code
#          trivially) but never encodes the load-bearing scenarios - resume-after-FF-before-journal,
#          resume-ignores-stale-segment-ref (W-1), and taskBase reset-retry. Scoped to the owning file.
$file = "tests/Guardrails.Integration.Tests/ResumeAndResetRetryTests.cs"
$text = Get-Content $file -Raw
$needles = @('trailer', 'taskBase')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch [regex]::Escape($n)) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "ResumeAndResetRetryTests is missing the load-bearing resume scenario term(s) [$($missing -join ', ')] - FF-trailer resume / W-1 / taskBase reset-retry not encoded"
    exit 1
}
exit 0
