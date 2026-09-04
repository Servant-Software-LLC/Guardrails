# catches: this plan building on a RED start. Tasks 02/03 modify RunEventStream, the exact surface
#          Category=RunEvents in Guardrails.Core.Tests covers. If those tests are ALREADY failing on
#          the starting code, task 03's tests-pass guardrail fails from pre-existing breakage, the
#          failure is misattributed, and its retries burn on a defect it did not cause. Scoped by
#          --filter to the area's EXISTING tests: a whole-project dotnet test here would hit the
#          #165/#176 compile-coupling trap once the mid-TDD stub tasks land.
# Plan!=36-onevent excludes THIS plan's about-to-be-authored red tests. It matches nothing today (no
#          test carries a Plan trait yet) and arms as tasks land - which is what keeps a --fresh or a
#          re-run after partial work from reading this plan's intentional red as pre-existing breakage.
#          This is the ONLY place the plan-wide trait appears alone (#455); every task-level filter
#          names its own test CLASS.
# Measured baseline (#478): Category=RunEvents in Guardrails.Core.Tests = 49 tests, all passing (re-measured on this branch; plan 35 saw 41 - master has moved).
#          Green on arrival is CORRECT here - this is a positive/assert-present preflight, the class
#          Step 7.0a exempts, not a work guardrail.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
    --filter "Category=RunEvents&Plan!=36-onevent" --nologo 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): key on the EXECUTED count (Passed + Failed), never Total (which counts
# [Skip]ped tests). A --filter that matches nothing exits 0 and would certify an empty set.
$passed = 0; $failed = 0
if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "PRECONDITION: the Core RunEvents filter executed ZERO tests - the filter, the trait or the test project moved. This preflight certifies nothing until it selects real tests."
    exit 1
}

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Pre-existing Core RunEvents failures (this plan has not run yet) ==="
    $log -split "`r?`n" | Where-Object {
        $_ -match '^\s*(Failed|Error Message|Expected|Actual|Stack Trace|\s+at )' -or $_ -match '\[FAIL\]'
    } | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The Core RunEvents tests are ALREADY failing on the starting code ($failed failed of $($passed + $failed) executed). Fix the pre-existing breakage before this plan builds on it - never build on red (#181)."
    exit 1
}

Write-Output "Baseline green: $passed Core RunEvents test(s) pass on the starting code."
exit 0
