# catches: wave 3 exiting without the harness actually provisioning the read-only grant its own retry
#          protocol names - the injection tests do not pass on the merged wave HEAD.
$log = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ToolGrant|FullyQualifiedName~ClaudePromptRunner" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "---- failure detail (why) ----"
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails') { Write-Output $line } }
    Write-Output "the grant-injection tests fail on the merged wave-3 HEAD"
    exit 1
}
exit 0
