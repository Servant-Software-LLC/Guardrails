# catches: wave 3 exiting without the harness actually provisioning the read-only grant its own retry
#          protocol names - the injection tests do not pass on the merged wave HEAD.
# scope: ToolGrantInjection ONLY. The filter used to also sweep in ~ClaudePromptRunner, which pulled
#        ClaudePromptRunnerArgsTests - a pre-existing golden no wave-3 task could edit - into this
#        gate's exit criteria (#193 orphaned-golden trap). A name-prefix filter is an accidental scope:
#        any future ClaudePromptRunner* test file would silently join wave 3's contract. Collateral
#        breakage is the job of wave 4's full-suite gate (02-all-tests-pass.ps1), not this one.
$log = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ToolGrantInjection" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
# A filter matching ZERO tests exits 0 - verified by execution against master, which prints
# "No test matches the given testcase filter" and returns exit code 0. The old broad filter could never
# be vacuous (pre-existing tests always matched); narrowing introduced that hole, so close it explicitly.
if ($log -match 'No test matches the given testcase filter') {
    Write-Output ""
    Write-Output "the filter matched ZERO tests - ToolGrantInjectionTests is absent from the merged wave-3 HEAD, so this gate certified NOTHING"
    exit 1
}
if ($code -ne 0) {
    Write-Output ""
    Write-Output "---- failure detail (why) ----"
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails|Expected:|Actual:|Strings differ') { Write-Output $line } }
    Write-Output "the grant-injection tests fail on the merged wave-3 HEAD"
    exit 1
}
exit 0
