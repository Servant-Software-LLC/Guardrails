# catches: tautological tests. The loader is UNCHANGED by this task, so every ActionDefinition it
#          produces carries TierOrigin.None. Tests that PASS against that verify nothing - they are
#          asserting the default value of a property nobody populates. With the build green
#          (guardrail 01), a non-zero exit here means THIS class's tests ran and FAILED = TDD red.
#          The --filter names this pair's OWN test class: a plan-wide-trait filter would go green off
#          a SIBLING's red tests whether or not these fail, degrading the red proof into merge-order
#          luck (#455). Sibling classes are red for most of this wave, so that is not hypothetical.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=TierResolution&FullyQualifiedName~ActionTierProvenanceTests'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# GUARD FIRST on the INVERSE check (#455) - the opposite order from the forward form, deliberately.
# Here a crashed/never-started test host also exits NON-ZERO, which is this check's SUCCESS condition:
# guard-second would certify TDD red over a run that executed nothing. Key on the EXECUTED count
# (Passed+Failed; Total: would also count [Skip]ped tests), never on a verbosity-dependent string.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "ZERO tests executed - the TDD-red proof certified nothing. MOST LIKELY CAUSE: the tests are missing the Trait attribute for Category = TierResolution, which is what this filter selects on - check that FIRST, and adding it IS an in-scope fix to your own test file. Other causes: the class name does not match '$filter', the filter is malformed, every matched test is [Skip]ped, or the test host failed to start (read the log above). What this is NOT is a tautology finding - do not weaken or delete assertions to make it pass."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output "the ActionTierProvenanceTests tests PASS against the UNCHANGED loader - they are tautological. PlanLoader still collapses action.tier and tiering.defaultTier (Tier = rawAction?.Tier ?? defaultTier) and never sets TierOrigin, so every loaded action carries TierOrigin.None. A test suite that goes green against that is asserting a property default, not the provenance behaviour. MOST LIKELY CAUSE: the tests construct an ActionDefinition by hand instead of driving the real PlanLoader over a plan folder on disk - a hand-built record asserts your own object initializer. Drive the loader."
    exit 1
}
exit 0
