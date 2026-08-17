# catches: a test-author task that wrote TAUTOLOGIES - tests that pass against a stub, proving
#          nothing about #230-lite. The stub is real and specific: JournalTierSpend's entry points
#          throw NotImplementedException, so every behavioural test must be red. If they all pass,
#          either the aggregation was implemented here (task 11's job) or the tests assert nothing.
# INVERSE guardrail: a NON-zero `dotnet test` exit is SUCCESS here. The sibling 01-build-passes has
# already established that the solution compiles, so a non-zero exit now unambiguously means the
# tests RAN and FAILED (#155). No #179 re-emit: the failures are the intended outcome.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=TierResolution&FullyQualifiedName~PerTierSpendTests'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# INVERSE polarity => ZERO-MATCH GUARD FIRST (#455): a crashed or never-started test host also exits
# non-zero. Keyed on the EXECUTED count (Passed+Failed); "Total:" counts [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests (wrong class name, or the class-level [Trait(`"Category`", `"TierResolution`")] is missing), is malformed, or every matched test is [Skip]ped."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output ""
    Write-Output "every PerTierSpendTests test PASSED against a JournalTierSpend whose entry points throw NotImplementedException - so the suite asserts nothing. Encode the aggregation, the ascending rung order, the tokens-only degradation and (above all) the Invariant 7 suppression rule, asserting on the RENDERED TEXT for the suppression cases."
    exit 1
}
exit 0
