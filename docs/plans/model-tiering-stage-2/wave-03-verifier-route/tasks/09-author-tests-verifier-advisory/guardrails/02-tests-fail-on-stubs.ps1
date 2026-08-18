# catches: tautological tests - tests that PASS against the stub verify nothing. With the build green
#          (guardrail 01), a non-zero exit here means THIS class's tests ran and FAILED = TDD red.
#          The --filter names this pair's OWN test class: a plan-wide-trait filter would go green off
#          a SIBLING's red tests whether or not these fail, degrading the red proof into merge-order
#          luck (#455). Sibling classes are red for most of this wave, so that is not hypothetical.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=TierResolution&FullyQualifiedName~VerifierAdvisoryTests'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# INVERSE polarity => ZERO-MATCH GUARD FIRST (#455): a crashed or never-started test host also exits
# non-zero. Key on the EXECUTED count (Passed+Failed); Total: would count [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "ZERO tests executed - the TDD-red proof certified nothing. MOST LIKELY CAUSE: the tests are missing the class-level Trait attribute for Category = TierResolution, which is what this filter selects on - check that FIRST, and adding it IS an in-scope fix to your own test file. Other causes: the class is not named VerifierAdvisoryTests, the filter is malformed, every matched test is [Skip]ped, or the test host failed to start."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output ""
    Write-Output "every VerifierAdvisoryTests test PASSED against the stub - so the suite asserts nothing. MOST LIKELY CAUSE: the suite asserts only that some advisory object exists. Encode the cases that need real logic: equal-and-STRONG produces NO advisory, the run PROCEEDS in every case, and the de-duplication rule - provenance always, a log line only when the JIT observation DIFFERS from the preflight's prediction."
    exit 1
}
exit 0
