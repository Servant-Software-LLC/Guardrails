# catches: a plan that starts on RED - the existing Guardrails.Core tests already failing before any
#          task runs, so a work task's tests-pass guardrail fails from PRE-EXISTING breakage and the
#          retries are spent misattributing it. Filtered to the CURRENTLY-GREEN existing tests only:
#          a whole-project run would hit the #165/#176 compile-coupling trap once the TDD tasks land.
#          Re-emits the failure DETAIL at the END so a red baseline's WHY reaches the halt feedback,
#          not just `[FAIL] <name>` (#179).
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)
# This `!=` exclusion is the ONE place the plan-wide trait stands ALONE (#455): it means "every existing
# test EXCEPT the ones this plan is about to author". Task-level guardrails must NOT copy this filter -
# there the trait is only ever the first term, conjoined with that pair's own test class.
$filter = 'Category!=ModelTieringStage1'
# No verbosity flag on the TEST command (#462): quiet verbosity suppresses the whole
# Error Message / Expected / Actual / Stack Trace block, leaving only "[FAIL] <name>" for the re-emit
# below to find - which defeats #179 by the flag alone. Quiet belongs on `dotnet build`, not here.
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, executed-count guard second (#455): a test host that never ran exits NON-zero with no
# summary at all, so checking the exit code first reports its real error instead of blaming the filter.
if ($code -ne 0) {
    $detail = $log |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40          # bound the block so it fits the ~60-line feedback tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "The area's existing tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it."
    exit 1
}

# EXECUTED-COUNT GUARD (#455). A baseline that executed ZERO tests exits 0 and certifies "the area is
# green" over nothing - the vacuous-baseline failure arriving through a filter typo rather than an empty
# project. Sum Passed+Failed; "Total:" would also count [Skip]ped tests, so it can read >= 1 having run
# nothing. Never key on "No test matches the given testcase filter" - that string is verbosity-dependent.
$ran = ([regex]::Matches(($log | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the baseline certified NOTHING. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. A green verdict here would be vacuous."
    exit 1
}
exit 0
