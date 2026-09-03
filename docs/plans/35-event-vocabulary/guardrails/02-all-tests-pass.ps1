# catches: a green per-task DAG whose CONTRIBUTIONS break each other once merged - the case each
#          task's own guardrails structurally cannot see, because every task is verified against its
#          own partial base. Both suites, on the merged plan-branch HEAD.
#          LOCAL (no scope key): a whole-suite run is a terminal postcondition, not a union invariant
#          (#125/#165) - at an intermediate union a downstream task's tests are legitimately red.
# Measured baseline (#478): Core 2423 passed / 0 skipped, Integration 1113 passed / 4 by-design skips
#          on e7ba57d. This plan must not lower either number.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$failures = New-Object System.Collections.Generic.List[string]

foreach ($proj in @(
    'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj',
    'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj')) {

    $log  = & dotnet test $proj --nologo 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Output "===== $proj ====="
    Write-Output $log

    $passed = 0; $failed = 0
    if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
    if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
    if (($passed + $failed) -lt 1) {
        $failures.Add("[$proj] executed ZERO tests - the test host did not run. A suite that runs nothing certifies nothing.")
        continue
    }
    if ($code -ne 0) {
        # #179: the WHY must reach the ~60-line retry/halt tail, not just the [FAIL] names.
        $detail = $log -split "`r?`n" | Where-Object {
            $_ -match '^\s*(Error Message|Expected|Actual|Stack Trace|\s+at )' -or $_ -match '\[FAIL\]'
        }
        $failures.Add("[$proj] $failed test(s) failed of $($passed + $failed) executed.")
        foreach ($d in $detail) { $failures.Add("    $d") }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Suite failures on the merged plan branch ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Both suites pass on the merged plan branch."
exit 0
