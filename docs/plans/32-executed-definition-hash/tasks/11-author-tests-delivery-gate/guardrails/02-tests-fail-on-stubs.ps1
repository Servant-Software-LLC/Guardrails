# catches: milestone C's acceptance criterion asserted as something weaker. Section 6.7 states P9
#          without hedging: "A run with a mid-run task.json edit, mergeOnSuccess ON, all tasks green:
#          nothing is merged to the user's branch, the plan branch retains the work, exit code is 2. An
#          implementation that passes every other bullet and still merges has not fixed the reported
#          defect." That is an assertion about DELIVERY and about the EXIT CODE, not about a report
#          field, and it must be observed FAILED on this tree - where the run delivers and exits 0.
#
#          It also catches the mode substitution. Section 8: these pins "cannot be faked - #382's lesson
#          is that a fake-masked unit guardrail certifies green while the real composition-root path is
#          broken, and the default execution mode for a real run is worktree mode." A P9 asserted against
#          a fake worktree provider proves nothing about the seam that actually delivers.
#
# ONE DECLARED EXEMPTION - P13 - and it is a regression pin, not a defect pin:
#   P13 AfterADivergenceHalt_TheWorkSurvivesOnThePlanBranch asserts that the diverged task's integration
#       commit is on the plan branch and its journal entry reads succeeded. Today a mid-run-edited run
#       goes green, so the commit and the journal entry are both there and a CORRECT test is GREEN. Its
#       job is to STAY green after stage 13, and it is the pin standing against the form of candidate (3)
#       the issue itself proposed: section 6.4 re-specifies "refuse to record a success" as "record the
#       success, block the delivery", because refusing discards paid work (#554's defect, fixed hours
#       before this plan was written) AND leaves a plan-branch commit whose journal says otherwise -
#       precisely the present-but-uncorroborated state Part C rule 3 refuses to rewind past, turning a
#       recoverable drift into a mandatory full reset.
#   It asserts Expect='Executed' (present in the TRX, not [Skip]ped). It stays IN the manifest: a dropped
#   row and an oversight look identical from the outside.
#
# WHAT THIS CENSUS CANNOT SEE: it proves each test is COUPLED to the delivery path, never that its
#          assertion is correct. P9 is the one where that residual is most expensive, because a P9 that
#          asserts exit 2 but never checks the user's branch would pass this census and still let a
#          divergence run DELIVER. Reading it is a human job at the draft review.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating (#455 companion (a)): no existing class in this project, and no other class this plan
# authors, contains 'DivergenceDeliveryGateTests' as a substring.
$filter = 'FullyQualifiedName~DivergenceDeliveryGateTests'

# THE MANIFEST: each pin -> the test method name the ACTION PROMPT PINNED for it. A BARE STRING means
# Expect='Failed'. A HASHTABLE declares an EXEMPTION - a row a CORRECT implementation leaves GREEN on
# this tree, so demanding red would demand a correct implementation fail. See the header for each one.
$manifest = [ordered]@{
    'P9  a green run with a mid-run edit does NOT deliver, exits 2'   = 'AGreenRunWithAMidRunDefinitionEdit_DoesNotDeliver_AndExitsTwo'
    'P11 the in-run halt and the next resume name the same set'       = 'TheInRunDivergenceAndTheNextResumesDrift_NameTheSameTaskSet'
    'P13 the work SURVIVES - commit on the branch, journal succeeded' = @{ Name = 'AfterADivergenceHalt_TheWorkSurvivesOnThePlanBranch'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr32-delivery-census-$PID"
# --results-directory is NOT cleared between runs: a stale TRX from a previous attempt would be read as
# THIS attempt's evidence.
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

# No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by cloning a
# sibling file (#462).
$out = & dotnet test tests/Guardrails.Integration.Tests --nologo --filter $filter `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (test host failed to
# start, wrong project path, or a MALFORMED --filter, which exits 0 SILENTLY). Diagnose THAT; falling
# through would print "every pin unbound", a confident wrong message aimed at the one artifact a retry
# agent here IS allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX carries a default xmlns, so SelectNodes('//UnitTestResult') returns
# NOTHING. The Where-Object is load-bearing: with zero tests executed the TRX has no <Results> element,
# the navigation yields $null, and @($null).Count is ONE - so the bare form would make the guard below
# evaluate 1 -lt 1 and never fire.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound pin, so ONE attempt learns every gap.
$failures = @()
foreach ($pin in $manifest.Keys) {
    $entry  = $manifest[$pin]
    $name   = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }

    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits a
    # [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$pin -> no test named '$name' ran (absent from the file, or not selected by the filter '$filter')"
        continue
    }

    if ($expect -eq 'Executed') {
        # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
        # treated as not-executed - never let a missing value read as satisfied.
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$pin -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - this file's header says why a correct implementation leaves it green) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all, and the exempt rows here are the regression pins."
        }
        continue
    }

    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$pin -> '$name' is $seen on this tree, not Failed. There is no divergence gate on this tree - stage 13 builds it and stage 15 renders it - so a correct pin MUST fail here. P9 is milestone C's ACCEPTANCE CRITERION: today the run delivers and exits 0. If P9 is green now, it is asserting something the current product already does, and an implementation that passes every other bullet and still merges would satisfy it. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pins are not proven RED on this tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Census clean: all $($manifest.Count) pins are bound to a pinned test with the declared outcome (2 Failed, 1 declared-exempt Executed)."
exit 0
