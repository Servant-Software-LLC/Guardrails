# catches: tautological tests - tests that PASS against the still-throwing Resolve stub verify
#          nothing. With the build green (guardrail 01), a non-zero exit here means THIS PAIR's tests
#          ran and FAILED = TDD red. The --filter names this pair's OWN test class: a plan-wide-trait
#          filter would go green off the SIBLING selection pair's tests whether or not these fail,
#          degrading the red proof into merge-order luck (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=TierResolution&FullyQualifiedName~TierResolverPrecedenceTests'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# GUARD FIRST on the INVERSE check (#455) - a crashed/never-started test host also exits NON-ZERO,
# which is this check's SUCCESS condition, so guard-second would certify "TDD red" over a run that
# executed nothing. Key on the EXECUTED count (Passed+Failed; "Total:" counts [Skip]ped too).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "ZERO tests executed - the TDD-red proof certified nothing. MOST LIKELY CAUSE: the tests are missing the Trait attribute for Category = TierResolution, which is what this filter selects on - check that FIRST, and adding it IS an in-scope fix to your own test file. Other causes: the class name does not match '$filter', the filter is malformed, every matched test is [Skip]ped, or the test host failed to start (read the log above). What this is NOT is a tautology finding - do not weaken or delete assertions to make it pass."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output "the TierResolverPrecedenceTests tests PASS against the still-throwing Resolve stub - they are tautological (no real precedence behavior is asserted)"
    exit 1
}
exit 0
