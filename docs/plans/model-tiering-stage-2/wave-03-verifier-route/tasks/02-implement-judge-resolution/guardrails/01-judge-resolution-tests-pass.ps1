# catches: an implementation whose behaviour deviates from the tests THIS task pair owns.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone (#455):
#          a trait-only filter asserts the state of every test in the plan, so this task could not go
#          green until tasks it does NOT depend on had run - a deadlock validate and graph --check
#          cannot see. Re-emits the assertion lines at the END so they reach the retry tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# The pair's own class FIRST (matching the inverse check), then the settled actor classes.
# Plus the SETTLED actor classes from waves 1-2, as a REGRESSION guard. This task extends
# TierResolver.cs, whose SelectCandidate/Resolve the prompt forbids it to change - and the
# wave-3 exit gate filters to the three NEW classes only, so without this a broken actor half
# would surface at the plan TERMINAL gate, after the whole wave had run. These two classes are
# already green and owned by earlier waves: including them is a regression check, not the #455
# over-broad shape (no sibling in THIS wave can turn them red or green).
$filter = 'Category=TierResolution&(FullyQualifiedName~JudgeResolutionTests|FullyQualifiedName~TierResolverCandidateSelectionTests|FullyQualifiedName~TierResolverPrecedenceTests)'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual block, so the re-emit
# below would have only test NAMES to re-emit and #179 is defeated by the flag alone (#462).
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output ""
    Write-Output "judge resolution is not to DoR 6.5/6.5.1. Two failures worth naming because their cause is not obvious from the assertion alone: if a BUMP test fails, the bump is in STRENGTH at the actor's RUNG - a tier bump satisfies 'stronger' and is exactly what D24a forbids. If the FLOOR test fails, tiering.verifier.minTier only ever RAISES a result that came out too low; it must never lower one that came out high."
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter matching nothing, or malformed, also exits 0.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}

# A TOTAL count cannot police this filter: the two settled ACTOR classes contribute dozens of tests,
# so any total-based floor is cleared by them even when THIS pair's JudgeResolutionTests contributed
# ZERO - which is exactly the case worth catching (a misnamed or untagged new class). Assert PER
# CLASS; --list-tests enumerates without running anything.
$listed = dotnet test tests/Guardrails.Core.Tests --filter 'FullyQualifiedName~JudgeResolutionTests' --list-tests --nologo 2>&1
$own = @(($listed | Out-String) -split "`r?`n" | Where-Object { $_ -cmatch 'JudgeResolutionTests' }).Count
if ($own -lt 1) {
    Write-Output "exit 0 and $ran test(s) ran, but ZERO of them are in JudgeResolutionTests - so this guardrail certified only the settled actor classes. Most likely the pair's own class is misnamed or missing its class-level Trait attribute for Category = TierResolution."
    exit 1
}
exit 0
