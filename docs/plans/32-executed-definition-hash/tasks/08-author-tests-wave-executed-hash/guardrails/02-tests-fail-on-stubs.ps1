# catches: a wave pin that is an ECHO JUDGE, or one that is green because it never reached the fold.
#          Section 5.8 is explicit: "Neither leg may compute its expected value by calling the production
#          pinned function - that is an echo-judge, green by construction. The test reconstructs the fold
#          independently, separators and labels included." A test that computes its expectation with the
#          function under test passes whatever that function does, including nothing.
#
#          Both pins must be observed FAILED in the runner's OWN TRX on this tree, and both genuinely are
#          red here, which is why this file has NO declared exemptions:
#            P7a: stage 5 pinned the TASK level; the wave fold still recomputes from disk. On an edited
#                 run the task stamped hash describes the PRE-edit bytes while the wave's describes the
#                 POST-edit ones - the two levels disagree about the same tasks in the same journal.
#                 Section 5.4: milestone A alone makes that disagreement HARDER to notice than it is
#                 today, because today both levels are consistently wrong.
#            P7b: the wave-GATE half. An implementation that folds task.DefinitionHashAtLoad for the task
#                 half but still walks the wave's guardrails and preflights folders from CURRENT DISK
#                 passes P7a exactly, while leaving the wave-level half of the defect intact. P7b is the
#                 only thing that separates them, and it is red today for the same reason.
#
#          A suite-level non-zero exit cannot carry either: with only two pins in the class, one failing
#          hollow sibling would hold the exit code for both.
#
# NO DECLARED EXEMPTIONS, and that is worth stating rather than leaving as an absence: both pins are
#          defect pins, both are red on this tree, and neither is a "nothing else moved" regression. The
#          regression half of milestone B lives in stage 9's guardrail, which re-runs the SHIPPED
#          WaveDefinitionHashTests to prove the disk-reading Compute(wave) survived beside the new
#          pinned fold rather than being replaced by it.
#
# WHAT THIS CENSUS CANNOT SEE: it proves each test is COUPLED to the fold, never that its reconstruction
#          of the fold is BYTE-CORRECT. A reconstruction with the wrong separator is red here and red
#          after stage 9 too - which at least fails loudly - but one that happens to agree with the
#          production fold for the wrong reason would pass both. Reading the two side by side is a human
#          job; section 5.8 names the duplication as a deliberate trade.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating (#455 companion (a)): 'WaveExecutedDefinitionHashTests' is contained by nothing else in
# this project. Note the containment runs the OTHER way - this class CONTAINS stage 1's
# 'ExecutedDefinitionHashTests', which is why stages 1 and 4 must namespace-qualify their filter and
# this one need not. The shipped 'WaveDefinitionHashTests' is a different, shorter name and is NOT
# selected here: it must keep passing untouched, and stage 9's guardrail runs it separately.
$filter = 'FullyQualifiedName~WaveExecutedDefinitionHashTests'

# THE MANIFEST: each pin -> the test method name the ACTION PROMPT PINNED for it. A BARE STRING means
# Expect='Failed'. A HASHTABLE declares an EXEMPTION - a row a CORRECT implementation leaves GREEN on
# this tree, so demanding red would demand a correct implementation fail. See the header for each one.
$manifest = [ordered]@{
    'P7a the wave hash moves IFF a constituent task hash moves'     = 'TheWaveHashChanges_IffAConstituentTaskHashChanges'
    'P7b a mid-run WAVE-GATE edit leaves the wave hash unmoved'     = 'TheStampedWaveHash_IsUnmoved_WhenAWaveGateFileIsEditedMidRun'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr32-wave-census-$PID"
# --results-directory is NOT cleared between runs: a stale TRX from a previous attempt would be read as
# THIS attempt's evidence.
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

# No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by cloning a
# sibling file (#462).
$out = & dotnet test tests/Guardrails.Core.Tests --nologo --filter $filter `
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
        $failures += "$pin -> '$name' is $seen on this tree, not Failed. Stage 5 has pinned the TASK level while the wave fold still recomputes TaskDefinitionHash.Compute(task) from disk, so the two levels DISAGREE about the same tasks in the same journal - which is precisely the state section 14.5's 'the wave hash changes iff a constituent task hash changes' asserts cannot happen. A correct pin MUST fail here. If it passes, the expected value was computed by calling a production hash function rather than reconstructed independently, which is an echo judge - green by construction and blind to the defect. ('NotExecuted' = [Fact(Skip=...)].)"
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
