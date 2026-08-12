# catches: the injection landing WITHOUT re-baselining the pre-existing arg-spelling golden it
#          invalidates (#193). This lives on the TASK, not the wave exit gate, deliberately:
#          ClaudePromptRunnerArgsTests.cs is in THIS task's writeScope, so a failure here is fixable by
#          the task that caused it. The wave gate must stay narrow - it may only assert what a wave task
#          can fix, which is what created the orphaned-golden halt in the first place.
$log = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ClaudePromptRunnerArgsTests" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($log -match 'No test matches the given testcase filter') {
    Write-Output ""
    Write-Output "the filter matched ZERO tests - ClaudePromptRunnerArgsTests is gone or was renamed away, so this gate certified NOTHING. Restore the class and re-baseline its assertions."
    exit 1
}
if ($code -ne 0) {
    Write-Output ""
    Write-Output "---- failure detail (why) ----"
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails|Expected:|Actual:|Strings differ') { Write-Output $line } }
    Write-Output "the pre-existing ClaudePromptRunnerArgsTests golden does not pass - RE-BASELINE the assertions your injection invalidates (preserve their intent; never delete them)"
    exit 1
}
exit 0
