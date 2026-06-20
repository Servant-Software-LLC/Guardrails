# catches: a vacuous LogsAndRunConfigTests that compiles-fails (passing tests-fail-on-current-code
#          trivially) on, say, only the worktreeRoot reference, while never encoding the logs-path or
#          maxParallelism-default-3 assertions. Assert all three concerns are present. Scoped to the
#          one file this task owns.
$file = "tests/Guardrails.Core.Tests/LogsAndRunConfigTests.cs"
$text = Get-Content $file -Raw
$needles = @('logs', 'runId', 'MaxParallelism|maxParallelism', 'worktreeRoot|WorktreeRoot', 'mergeOnSuccess|MergeOnSuccess')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch $n) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "LogsAndRunConfigTests is missing the required concern(s) [$($missing -join ', ')] - logs elevation + RunConfig additions not all encoded"
    exit 1
}
exit 0
