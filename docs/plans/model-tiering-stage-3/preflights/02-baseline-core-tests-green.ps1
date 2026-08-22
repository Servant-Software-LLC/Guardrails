# catches: a brownfield plan building on a RED base - the EXISTING tests in Guardrails.Core.Tests, the
#          area every wave-1 task modifies (Loading/DiagnosticCodes.cs, Loading/PlanValidator.cs), are
#          already failing on the starting code. Asserting them green BEFORE the DAG means a later
#          task's tests-pass failure is attributable to THAT task rather than to pre-existing breakage,
#          and a new test's red is unambiguous (#181). Re-emits the failure DETAIL at the END so a red
#          baseline's WHY reaches the halt feedback, not just `[FAIL] <name>` (#179, dotnet.md §4.2).
# The `!=` exclusion is the ONE place the bare plan-wide trait is correct (#455): it selects the
# PRE-EXISTING tests only, so this can never go red on the intentionally-failing tests waves 1-3 author.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)
$filter = 'Category!=ModelTieringStage3'
# NO -v q: it suppresses the Error Message / Expected / Actual / Stack Trace block, leaving the re-emit
# below nothing but test NAMES to re-emit, which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary at
# all, so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the existing tests in tests/Guardrails.Core.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0, and a "baseline green" verdict certified by a run that executed NOTHING
# is the catalogue's vacuous-baseline warning made concrete. Key on the EXECUTED count (Passed+Failed);
# "Total:" would also count [Skip]ped tests. Never on "No test matches ..." (verbosity-dependent, #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests or is malformed. Check it against tests/Guardrails.Core.Tests."
    exit 1
}
exit 0
