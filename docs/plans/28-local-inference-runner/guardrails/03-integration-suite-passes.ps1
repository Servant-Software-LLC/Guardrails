# catches: a merged HEAD where the WHOLE Integration suite is not green - the only moment "all tests pass" is
#          an honest question, and the only place this skill permits it (never on a task).
#          LOCAL by design - no scope key. At an intermediate union a downstream task has not run, so the
#          suite legitimately fails there; tagging this scope:"integration" would red-halt a correct run
#          (#125/#165). This is the terminal postcondition, evaluated ONCE on the fully merged branch.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$log = & dotnet test 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj' --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Integration suite failure detail (re-emitted at the END so the WHY reaches the operator, #179) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*(\[FAIL\]|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "The Integration suite is not green on the merged HEAD."
    exit 1
}

$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed
if ($executed -lt 1) {
    Write-Output "THE Integration SUITE EXECUTED ZERO TESTS. An exit code of 0 over an empty run certifies nothing - the test host did not start, or the project no longer discovers tests."
    exit 1
}

Write-Output "Integration suite green on the merged HEAD: $executed executed, 0 failed."
exit 0
