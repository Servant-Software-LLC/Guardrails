# catches: a green per-task DAG whose CONTRIBUTIONS break each other once merged - the case each
#          task's own guardrails structurally cannot see, because every task is verified against its
#          own partial base. Both suites, on the merged plan-branch HEAD.
#          LOCAL (no scope key): a whole-suite run is a terminal postcondition, not a union invariant
#          (#125/#165) - at an intermediate union a downstream task's tests are legitimately red.
# Measured baseline (#478): Core 2425 passed / 0 skipped, Integration 1113 passed / 4 by-design skips
#          on e7ba57d. This plan must not lower either number.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# Count FLOORS. Task 02 is instructed to RETIRE the reflection halves of two test files, and deleting
# them wholesale is inside its writeScope - a suite with fewer tests still goes GREEN, so nothing else in
# this plan can see a coverage deletion. Measured on 4e4785e: Core 2423, Integration 1113 (+4 by-design
# skips). The +1/+3 the retired halves carried are subsumed by the new exhaustive sweep, so the floor
# does not rise for them. Core reads 2425, not the 2423 measured before this plan existed: registering
# the plan folder in ProducerCoverageCorpusTests (the closed-world tripwire that made this gate RED ON
# ARRIVAL - see the review report) added two pinned cases. The floor is the count WITH the folder
# present, which is the only count this gate will ever see.
$floors = @{
    'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'               = 2425
    'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj' = 1113
}

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
    if ($passed -lt $floors[$proj]) {
        $failures.Add("[$proj] only $passed test(s) passed; this plan was authored against $($floors[$proj]). Tests were deleted, disabled, or renamed out of existence somewhere in this plan - and a smaller green suite is still green, so this floor is the only check that sees it.")
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
