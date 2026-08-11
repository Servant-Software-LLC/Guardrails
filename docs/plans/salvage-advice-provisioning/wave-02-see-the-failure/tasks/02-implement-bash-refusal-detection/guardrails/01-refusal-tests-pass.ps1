# catches: the scanner still blind - the authored Bash-refusal tests do not pass, so refused commands
#          keep registering as zero permission walls.
$log = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ClaudePermissionScannerBashRefusalTests" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "---- failure detail (why) ----"
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails') { Write-Output $line } }
    Write-Output "the Bash-refusal detection tests still fail"
    exit 1
}
exit 0
