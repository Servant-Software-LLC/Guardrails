# catches: an implementation whose behaviour deviates from the tests THIS task pair owns. The --filter
#          names this pair's OWN test class, never the plan-wide trait alone - a trait-only filter
#          asserts the state of every test in the plan, so this task could not go green until a task
#          that DEPENDS on it has run (a deadlock validate and graph --check cannot see, #455).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# The SAME $filter string as this pair's inverse half (task 02's census), copied verbatim so the two
# halves of the TDD pair can never drift apart.
$filter = 'Category=ModelTieringStage3&FullyQualifiedName~TieringRegistryWarningTests'
# NO -v q on the TEST command: it suppresses the Error Message / Expected / Actual / Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find, which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary, so
# checking the exit code first reports its real error instead of confidently blaming the filter - a
# misdiagnosis that would point the retry agent at the one artifact it is NOT allowed to edit here.
if ($testExit -ne 0) {
    $detail = $out |
        # `error CS\d+` is in the alternation because this task has no separate build guardrail and
        # `dotnet test` builds first: without it a COMPILE failure re-emitted nothing (none of the
        # assertion tokens match `error CS0101`) and the trailer below confidently misdiagnosed it as
        # a spec deviation. Same pattern the two build guardrails in this plan already use.
        Select-String -Pattern 'error [A-Z]{2}\d+|\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    if (($out | Out-String) -match 'error [A-Z]{2}\d+') {
        Write-Output "the test project did not COMPILE - fix the build error above. This is not a spec deviation; the tests never ran."
    } else {
        Write-Output "TieringRegistryWarningTests failing - GR2051/GR2052 are not emitted to spec (see failure details above)"
    }
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
# [Skip]ped tests. Never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against TieringRegistryWarningTests."
    exit 1
}
exit 0
