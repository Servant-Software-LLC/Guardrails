# catches: a vacuous MergeOnSuccessTests that compiles-fails (passing tests-fail-on-current-code
#          trivially on, say, only the flag reference) while never encoding the load-bearing §5
#          scenarios - the plan-branch -> original-branch delivery AND the load-bearing safety case:
#          AI-merge is WITHHELD at this boundary, so a conflicting user-branch advance HALTS to
#          needs-human with both branches intact (no force-overwrite). Assert each concern is present.
#          Scoped to the one file this task owns (grep-scope rule - no project-tree greps).
$file = "tests/Guardrails.Integration.Tests/MergeOnSuccessTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the merge-on-success test file was not authored"
    exit 1
}
$text = Get-Content $file -Raw
$needles = @{
    '(?i)merge-on-success|mergeOnSuccess|MergeOnSuccess' = 'no merge-on-success term - the plan-branch -> original-branch delivery scenario is missing'
    '(?i)withheld|needs-human|needsHuman'                = 'no withheld/needs-human term - the AI-merge-WITHHELD-at-this-boundary halt (conflicting user-branch advance -> needs-human, branches intact) is missing'
}
foreach ($n in $needles.Keys) {
    if ($text -notmatch $n) {
        Write-Output "MergeOnSuccessTests: $($needles[$n])"
        exit 1
    }
}
exit 0
