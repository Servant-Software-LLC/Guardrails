# catches: this plan building on a RED start. Every task here modifies the RunEvents surface
#          (RunEventStream, ObserverProjection, the decorators, AttachCommand). If those tests are
#          ALREADY failing on the starting code, a work task's tests-pass guardrail fails from
#          pre-existing breakage, the failure is misattributed to the task, and its retries burn on a
#          defect it did not cause. Scoped by --filter to the area's EXISTING tests: a whole-project
#          dotnet test here would hit the #165/#176 compile-coupling trap once mid-TDD tasks land.
# Measured baseline (#478): Category=RunEvents in Guardrails.Core.Tests = 41 tests, all passing, ~3s
#          on e7ba57d. Green on arrival is CORRECT and expected here - this is a positive/assert-present
#          preflight (the class Step 7.0a exempts), not a work guardrail.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
    --filter "Category=RunEvents" --nologo 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): key on the EXECUTED count (Passed + Failed), never Total (which counts
# [Skip]ped tests). A --filter that matches nothing exits 0 and would certify an empty set.
$passed = 0; $failed = 0
if ($log -match 'Passed:\s+(\d+)')  { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s+(\d+)')  { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "PRECONDITION: the RunEvents filter executed ZERO tests - the filter or the test project moved. This preflight certifies nothing until it selects real tests."
    exit 1
}

if ($code -ne 0) {
    # #179: re-emit the failure detail at the END so the WHY reaches the halt feedback, not just [FAIL].
    Write-Output ""
    Write-Output "=== Pre-existing RunEvents failures (this plan has not run yet) ==="
    $log -split "`r?`n" | Where-Object {
        $_ -match '^\s*(Failed|Error Message|Expected|Actual|Stack Trace|\s+at )' -or $_ -match '\[FAIL\]'
    } | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The RunEvents tests are ALREADY failing on the starting code ($failed failed of $($passed + $failed) executed). Fix the pre-existing breakage before this plan builds on it - never build on red (#181)."
    exit 1
}

Write-Output "Baseline green: $passed RunEvents test(s) pass on the starting code."
exit 0
