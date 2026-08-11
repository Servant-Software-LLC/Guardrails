# catches: the corrected salvage advice not actually landing - the tests that pin patch-first routing
#          and the granted-commands-only invariant still fail.
$log = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~RetryPolicySalvageAdviceTests|FullyQualifiedName~RetryPolicyTests" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "---- failure detail (why) ----"
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails') { Write-Output $line } }
    Write-Output "the salvage-advice tests (or the existing #374 regression) still fail"
    exit 1
}
exit 0
