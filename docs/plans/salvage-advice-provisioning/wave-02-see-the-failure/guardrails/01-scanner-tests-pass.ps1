# catches: wave 2 exiting with the permission scanner still blind to Bash refusals - the tests that
#          pin the real refusal phrases do not actually pass on the merged wave HEAD.
$log = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ClaudePermissionScanner" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "---- failure detail (why) ----"
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails') { Write-Output $line } }
    Write-Output "the permission-scanner tests fail on the merged wave-2 HEAD"
    exit 1
}
exit 0
