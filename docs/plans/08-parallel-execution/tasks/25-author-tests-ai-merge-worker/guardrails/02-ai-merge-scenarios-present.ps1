# catches: a vacuous AiMergeWorkerTests that compiles-fails (passing tests-fail-on-current-code
#          trivially) but never encodes the load-bearing scenarios - the GUARDRAILS_MERGE_OUT byte
#          contract, the blast-radius / out-of-bounds rejection, the markers-left rejection
#          (git diff --check gate i), and the ai-deleted-hunk -> colliding-sibling re-verify (B-3).
#          Scoped to the one file this task owns. (2nd review: added the Test-Path guard mirroring
#          21/23 so a missing file fails with a clear message instead of a Get-Content throw, and a 4th
#          needle for the markers-left / git diff --check scenario - gate i was previously unpinned.)
$file = "tests/Guardrails.Integration.Tests/AiMergeWorkerTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the AI-merge worker test file was not authored"
    exit 1
}
$text = Get-Content $file -Raw
$needles = @{
    'GUARDRAILS_MERGE'                          = 'no GUARDRAILS_MERGE_* term - the merge env / byte contract (GUARDRAILS_MERGE_OUT) scenario is missing'
    'porcelain|blast|out-of-bounds|OutOfBounds' = 'no blast-radius term - the out-of-bounds-write rejection (gate ii, git status --porcelain) scenario is missing'
    'sibling'                                   = 'no sibling term - the ai-deleted-hunk -> colliding-sibling re-verify (B-3) scenario is missing'
    'diff --check|markers|conflict marker|ConflictMarker' = 'no markers-left term - the markers-remaining rejection (gate i, git diff --check) scenario is missing'
}
$missing = @()
foreach ($n in $needles.Keys) {
    if ($text -notmatch $n) { $missing += $needles[$n] }
}
if ($missing.Count -gt 0) {
    Write-Output ("AiMergeWorkerTests is missing load-bearing AI-merge scenario(s):`n  - " + ($missing -join "`n  - "))
    exit 1
}
exit 0
