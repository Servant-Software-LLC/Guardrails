# catches: the harness still not provisioning the verb its own retry text names - the injection tests
#          do not pass, so the protocol keeps depending on a plan author or an operator dotfile.
$log = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ToolGrantInjectionTests" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($log -match 'No test matches the given testcase filter') {
    Write-Output ""
    Write-Output "the filter matched ZERO tests - task 01's ToolGrantInjectionTests is missing, so this gate certified NOTHING"
    exit 1
}
if ($code -ne 0) {
    Write-Output ""
    Write-Output "---- failure detail (why) ----"
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails|Expected:|Actual:|Strings differ') { Write-Output $line } }
    Write-Output "the grant-injection tests still fail"
    exit 1
}
exit 0
