# catches: a merged plan branch whose suite is red - every task green in isolation, the union broken.
#          Re-emits the assertion/exception lines at the END so they reach the harness feedback tail
#          (the last ~60 lines of stdout) - the tail would otherwise show WHAT failed, not WHY (#179).
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)
# No verbosity flag on the TEST command (#462): quiet verbosity suppresses the whole
# Error Message / Expected / Actual / Stack Trace block, leaving only "[FAIL] <name>" for the re-emit
# below to find - which defeats #179 by the flag alone. Quiet belongs on `dotnet build` (01), not here.
$log = & dotnet test Guardrails.sln --nologo 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, executed-count guard second (#455): a test host that never ran exits NON-zero with no
# summary at all, so checking the exit code first reports its real error instead of blaming the run.
if ($code -ne 0) {
    $detail = $log |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40          # bound the block so it fits the ~60-line feedback tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "The merged plan branch has failing tests."
    exit 1
}

# EXECUTED-COUNT GUARD (#455). Exit 0 alone does not mean the suite passed - a run that discovered and
# executed NOTHING also exits 0, and a terminal "whole suite green" verdict over zero tests is the
# vacuous-baseline failure at the loudest possible place. Sum Passed+Failed across every assembly's
# summary line; "Total:" would also count [Skip]ped tests, so it can read >= 1 having run nothing.
$ran = ([regex]::Matches(($log | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed across Guardrails.sln - the terminal suite gate certified NOTHING. Check that the test projects are registered in the solution and that discovery is not failing."
    exit 1
}
exit 0
