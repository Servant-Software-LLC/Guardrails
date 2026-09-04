# catches: a green per-task DAG whose CONTRIBUTIONS break each other once merged - the case each task's
#          own guardrails structurally cannot see, because every task is verified against its own
#          partial base. Both suites, on the merged plan-branch HEAD.
#          LOCAL (no scope key): a whole-suite run is a terminal postcondition, not a union invariant
#          (#125/#165) - at an intermediate union a downstream TDD task's tests are legitimately red.
# NO count floor, deliberately (#468): an executed-test COUNT is not an adequacy floor, and unlike plan
#          35 no task here is instructed to RETIRE tests, so there is no deletion for a floor to catch.
#          The zero-match guard below is NOT a floor - it only proves the run happened.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$failures = @()
foreach ($proj in @('tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj',
                    'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj')) {
    $log  = & dotnet test $proj --nologo 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Output "===== $proj ====="
    Write-Output $log

    $passed = 0; $failed = 0
    if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
    if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
    if (($passed + $failed) -lt 1) {
        $failures += "[$proj] executed ZERO tests - the run did not happen (a build failure or a moved project), so this gate certifies nothing."
        continue
    }
    if ($code -ne 0) {
        # #179: re-emit the failure DETAIL at the END so the WHY reaches the ~60-line retry tail.
        $detail = $log -split "`r?`n" | Where-Object {
            $_ -match '^\s*(Failed|Error Message|Expected|Actual|Stack Trace|\s+at )' -or $_ -match '\[FAIL\]'
        }
        $failures += "[$proj] $failed of $($passed + $failed) executed test(s) FAILED on the merged plan branch:`n" + ($detail -join "`n")
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Whole-suite failures on the merged plan branch ($($failures.Count) project(s)) ==="
    $failures | ForEach-Object { Write-Output $_; Write-Output "" }
    exit 1
}
Write-Output "Both suites pass on the merged plan branch."
exit 0
