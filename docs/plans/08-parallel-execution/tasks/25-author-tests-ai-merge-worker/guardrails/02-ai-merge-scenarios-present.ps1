# catches: a vacuous AiMergeWorkerTests that compiles-fails (passing tests-fail-on-current-code
#          trivially) but never encodes the load-bearing scenarios - the GUARDRAILS_MERGE_OUT byte
#          contract, the blast-radius / out-of-bounds rejection, and the ai-deleted-hunk →
#          colliding-sibling re-verify (B-3). Scoped to the one file this task owns.
$file = "tests/Guardrails.Integration.Tests/AiMergeWorkerTests.cs"
$text = Get-Content $file -Raw
$needles = @('GUARDRAILS_MERGE', 'porcelain|blast|out-of-bounds|OutOfBounds', 'sibling')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch $n) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "AiMergeWorkerTests is missing the load-bearing AI-merge scenario term(s) [$($missing -join ', ')] - merge-env-contract / blast-radius / B-3 colliding-sibling re-verify not encoded"
    exit 1
}
exit 0
