# catches: a re-baseline that changed the WORDS and not the CONTRACT. Both rewritten assertions must be
#          observed FAILED on today's tree, in the runner's OWN TRX - never merely discovered by name,
#          which an untouched assertion satisfies exactly as a comment satisfies a token floor (#375).
#          The specific wrong implementations it closes:
#            - :209 left as Assert.NotEqual (or softened to Assert.NotNull / "the hash changed"), which
#              is GREEN today with the defect fully intact - and would then be green forever, because
#              the whole point of stage 5 is to make hashAtStart and the recorded hash the same value;
#            - :77 left as Assert.True, likewise green today, so the divergence gate stage 13 builds
#              would have nothing gating it in the one shipped suite that exercises a mid-run edit.
#          A suite-level non-zero exit cannot tell either case apart from a correct rewrite, because
#          three sibling facts in the same class are green and one of them would carry the exit code.
#
# NO DECLARED EXEMPTIONS in this file's manifest, and that is the point of it. The three facts this
#          stage must NOT touch are deliberately absent from the manifest below: the census's business
#          is the enumerated behaviours only (dotnet.md section 4.4), and asserting anything about the
#          untouched three here would turn a scoped census into a whole-suite outcome audit. Guardrail
#          03 is what protects them, structurally.
#
# WHAT THIS CENSUS CANNOT SEE: it proves each assertion is COUPLED to the behaviour (it fails when the
#          behaviour is absent), never that the rewrite is CORRECT. An assertion inverted to something
#          arbitrary but false - Assert.Equal("x", recorded) - is red here too. Reading the two lines is
#          a human job, and section 15.1 spells out exactly what they must become.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating (#455 companion (a)): no other class in this project - shipped or authored by this
# plan - contains 'PlanEditedDuringRunTests' as a substring.
$filter = 'FullyQualifiedName~PlanEditedDuringRunTests'

# THE MANIFEST: the two section 15.1 rows this stage owns, bound to the method each lives in. Rows 3-5
# are STAGE 14's and are not listed here.
$manifest = [ordered]@{
    'row 1 :209 the recorded hash EQUALS the load-time value'   = 'AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges'
    'row 2 :77  a mid-run guardrail edit stops the run green'   = 'AGuardrailEditedMidRun_EmitsExactlyOneObservedPlanEditDecision'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr32-rebaseline-census-$PID"
# --results-directory is NOT cleared between runs: a stale TRX would be read as THIS attempt's evidence.
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

# No -v q on a test command (#462).
$out = & dotnet test tests/Guardrails.Integration.Tests --nologo --filter $filter `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, or a MALFORMED --filter, which exits 0 SILENTLY). Falling through would
# print "both rows unbound", a confident wrong message aimed at the one artifact a retry agent may edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX carries a default xmlns. The Where-Object is load-bearing: with zero tests
# executed the TRX has no <Results> element, the navigation yields $null, and @($null).Count is ONE - so
# the bare form would make the guard below evaluate 1 -lt 1 and never fire.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per row, so ONE attempt learns every gap.
$failures = @()
foreach ($row in $manifest.Keys) {
    $name = $manifest[$row]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits a
    # [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$row -> no test named '$name' ran. It is a SHIPPED method of this class: if it is absent, it was DELETED or RENAMED, both of which section 15.1 forbids in this stage."
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$row -> '$name' is $seen on today's tree, not Failed. The assertion's SENSE did not actually invert, or it was softened to something today's behaviour already satisfies. Section 15.1 is exact: :209 becomes Assert.Equal(hashAtStart, recorded); :77 becomes Assert.False(report.AllSucceeded, ...). ('NotExecuted' = [Fact(Skip=...)], which this stage forbids.)"
    }
}

Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) re-baselined rows are not proven RED on today's tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Census clean: both section 15.1 rows this stage owns are bound to a shipped method observed Failed on today's tree."
exit 0
