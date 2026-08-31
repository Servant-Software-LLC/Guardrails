# catches: a HOLLOW pin - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, a fixture that never reaches PlanValidator). It
#          PASSES against today's code and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit certifies the file honest while proving nothing (#375). One entry
#          per pin, each observed Failed in the runner's OWN TRX - never merely discovered by name,
#          which a hollow body satisfies exactly as a comment satisfies a token floor.
#
# DECLARED EXEMPTIONS - pins 6 and 7, and the reason is structural rather than convenient:
#          Pin6 APlanWithNoHandoffTable_LeavesTheDiagnosticListUNCHANGED and
#          Pin7 ACellOfBacktickedNonPaths_LeavesTheDiagnosticListUNCHANGED both assert that the FULL
#          diagnostic list is UNCHANGED. That is true today - no check exists, so nothing is added -
#          and it must STAY true after stage 5. A CORRECT test is therefore GREEN on current code, and
#          demanding red would demand a correct implementation fail. They assert Expect='Executed'
#          (present in the TRX, not [Skip]ped). They stay IN the manifest: a dropped row and an
#          oversight look identical from the outside, and these two are the pins plan 31 section 9
#          calls "the ones most at risk".
#
#          Two of nine exempt is the honest ratio here. If a later edit pushes it much past that, the
#          red census has become a forward one wearing its name - which is the signal to re-read the
#          split, not to add another exemption.
#
# WHAT THIS CENSUS CANNOT SEE, stated so a green reading is not over-read: it proves each pin is
#          COUPLED to the code path (it fails when the check is absent), never that its ASSERTION is
#          correct. A pin that builds a fixture, calls PlanValidator, and asserts the wrong CODE is
#          red today and green after - and passes. That is what guardrail 03 is for on the one axis
#          that matters most here (pins 1 and 2 keying GR2069), and it is a human read for the rest.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests'
$filter  = 'FullyQualifiedName~HandoffScopeCoverageTests'

# A BARE STRING means Expect='Failed' - the default. A HASHTABLE declares an EXEMPTION.
$manifest = [ordered]@{
    'pin 1  the REAL row-7 catch, and it is GR2069'                    = 'Row7WhoseOwningTaskHoldsOnlyTwoOfFourPaths_EmitsGR2069NamingTheCoveringTask'
    'pin 2  the REAL row-1 catch, both directions, and it is GR2069'   = 'Row1WithoutTheTestGlobEmitsGR2069_AndIsSilentOnceTheGlobIsAdded'
    'pin 3  the unreachable case, GR2068, no suggested correction'     = 'ConcretePathNoTaskCanWrite_EmitsGR2068WithNoSuggestedCorrection'
    'pin 3a the codes are mutually exclusive per row'                  = 'AnUnreachableRowEmitsGR2068AndNoGR2069'
    'pin 4  the anchor discriminator, both halves, ONE finding'        = 'AnchoredUnmatchedAndUnanchoredFragmentInOneCell_EmitExactlyOneFinding'
    'pin 5a the glob arm argument direction'                           = 'GlobCandidateCoveredByAConcreteScopeEntry_IsSilent'
    'pin 5b segment-aligned suffix, not substring'                     = 'SegmentAlignedSuffixMatches_ButASubstringOfASegmentDoesNot'
    'pin 6  no table => the FULL diagnostic list is unchanged'         = @{ Name = 'APlanWithNoHandoffTable_LeavesTheDiagnosticListUNCHANGED'; Expect = 'Executed' }
    'pin 7  prose cells => the FULL diagnostic list is unchanged'      = @{ Name = 'ACellOfBacktickedNonPaths_LeavesTheDiagnosticListUNCHANGED'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr31-handoff-census-$PID"
# --results-directory is NOT cleared between runs: a stale TRX would be read as this attempt's evidence.
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

# No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by cloning (#462).
$out = & dotnet test $project --nologo --filter $filter `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (test host failed
# to start, wrong project path, or a MALFORMED --filter, which exits 0 SILENTLY). Diagnose THAT;
# falling through would print "all nine pins unbound", a confident wrong message aimed at the one
# artifact a retry agent IS allowed to edit here - the test file.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the pins: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX carries a default xmlns, so SelectNodes('//UnitTestResult') returns
# NOTHING. The Where-Object is load-bearing: with zero tests executed the TRX has no <Results> element,
# the navigation yields $null, and @($null).Count is ONE - so the bare form would make the guard below
# evaluate 1 -lt 1 and never fire.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the pins: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound pin, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $entry  = $manifest[$behaviour]
    $name   = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }

    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits
    # a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter '$filter')"
        continue
    }

    if ($expect -eq 'Executed') {
        # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
        # treated as not-executed - never let a missing value read as satisfied.
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - this file's header says why a correct pin is green on today's code) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt pin still has to run; skipping it turns the exemption into no coverage at all, and these two are the silence pins."
        }
        continue
    }

    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on today's code, not Failed. src/Guardrails.Core/Loading/HandoffScopeCoverage.cs does not exist and PlanValidator runs no such check, so NOTHING can emit GR2068 or GR2069 yet - a pin that does not fail here never reached PlanValidator, or asserts something the check was never going to decide. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pins are not proven RED on today's code ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Census clean: all $($manifest.Count) pins are bound to a pinned test with the declared outcome (7 Failed, 2 declared-exempt Executed)."
exit 0
