# catches: the re-baseline that section 15.1 calls "the one that would have cost a run". Both rows this
#          stage owns assert on surfaces only stage 15 may change - the exit code and the literal
#          advisory text RunCommand.RenderPlanEditWarning emits - and an earlier draft of the plan put
#          them in stage 2, twelve stages away from their implementer, with the red landing on the one
#          stage that cannot fix it.
#
#          The stall was not the worst outcome. The cheapest green leaves these rows PASSING: an
#          implementer who never touches the advisory ships a harness that prints
#
#              Nothing was halted and nothing was re-run.
#
#          beside exit 2 and a blocked delivery - a message that is now false, on the exact surface this
#          plan exists to make honest, in a product whose thesis is that nothing is marked done
#          unverified. Pairing the string and its assertions into one author-tests -> implement pair
#          (this stage and stage 15) is what removes that option, and this census is what proves the
#          pairing was real rather than nominal.
#
#          Note the literal is uppercase in the source - RenderPlanEditWarning writes "POST-edit" - and
#          the shipped assertion uses StringComparison.OrdinalIgnoreCase, which is why it matches today.
#          Keep the comparison; invert the CLAIM.
#
# NO DECLARED EXEMPTIONS. Both rows are red on this tree and both must be: row 3 asserts exit 2 and a
#          FALSE delivery record (stage 13 gave it the second half; only stage 15 gives it the first),
#          and rows 4-5 assert an advisory that does not exist yet. The three facts this stage must NOT
#          touch are deliberately absent from the manifest - asserting anything about them here would
#          turn a scoped census into a whole-suite outcome audit, and guardrail 03 is what protects them.
#
# WHAT THIS CENSUS CANNOT SEE: it proves each assertion is COUPLED to the behaviour, never that the
#          rewrite says the RIGHT thing. Section 9 asks the halt text to name all three facts an operator
#          needs - which files moved, that the task ran the PINNED bytes, and that task.json is held from
#          load while prompts and guardrail scripts are not - and this is the one place a half-true
#          message actively misleads. Reading it is a human job.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating (#455 companion (a)): no other class in this project contains 'PlanEditedDuringRunTests'
# as a substring. The filter selects all five of its facts; the manifest below lists only the two this
# stage owns, because a census's business is the enumerated behaviours only (dotnet.md 4.4). The other
# three are protected structurally by guardrail 03.
$filter = 'FullyQualifiedName~PlanEditedDuringRunTests'

# THE MANIFEST: each pin -> the test method name the ACTION PROMPT PINNED for it. A BARE STRING means
# Expect='Failed'. A HASHTABLE declares an EXEMPTION - a row a CORRECT implementation leaves GREEN on
# this tree, so demanding red would demand a correct implementation fail. See the header for each one.
$manifest = [ordered]@{
    'row 3 :161/:167 exit 2 and Delivered == false'                 = 'ARunCarryingOnlyAPlanEditObservation_HaltsWithExitTwoAndDoesNotDeliver'
    'rows 4-5 :251/:257 the advisory states the PRE-edit contract'  = 'TheRenderedText_CarriesAllThreeSection51Consequences'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr32-advisory-census-$PID"
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
        $failures += "$pin -> '$name' is $seen on this tree, not Failed. Stage 13 has landed the gate but stage 15 has not yet touched RunCommand, so on this tree the run still exits 0 and the advisory still says the POST-edit hash is recorded and that nothing was halted. A correct rewrite MUST fail here. If it passes, the assertion's sense did not actually invert - and the cheapest green leaves these rows PASSING, shipping a harness that prints 'Nothing was halted and nothing was re-run' beside exit 2 and a blocked delivery, which is section 15.1's whole reason for pairing them with stage 15. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pins are not proven RED on this tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Census clean: all $($manifest.Count) pins are bound to a pinned test with the declared outcome (2 Failed, 0 exempt)."
exit 0
