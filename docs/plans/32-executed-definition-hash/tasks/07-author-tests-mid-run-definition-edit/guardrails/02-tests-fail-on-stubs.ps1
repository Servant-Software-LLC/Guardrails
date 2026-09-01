# catches: a HOLLOW integration pin - named for the behaviour, body a tautology, or quietly asserted in
#          the wrong execution MODE. P2 is the pin section 5.8 exists for: "an implementation that fixes
#          AttemptJournaler.cs alone passes the issue's own pin while leaving the default execution mode
#          broken." Stage 4 has just made exactly that implementation real in this tree - the serial
#          sites are pinned and the worktree sites are not - so P2 must be observed FAILED here, in the
#          runner's OWN TRX. A suite-level non-zero exit cannot carry that: three of this file's four
#          pins are green by design, and any one of them could be the one holding the exit code.
#
#          It also catches the quieter substitution: P2 written against the SERIAL settle. Section 8:
#          "a design that proved this only in serial mode would have proved it in the mode plan 28 did
#          not use." A serial-mode P2 is green on this tree, and the census is what notices.
#
# THREE DECLARED EXEMPTIONS - P3, P6a and P6b - and the reason is structural rather than convenient:
#   P3  TheTrailerAgreesWithTheJournal_OnARealGitSegment: today the trailer and the journal are both
#       stamped from the same settle-time recompute, so they already agree; after stage 5 both come from
#       the same pin, so they still agree. A CORRECT test is GREEN on both sides. Its job is to keep
#       Part C rule 3 trailer corroboration sound across the change, not to fail before it.
#   P6a TheDriftPrePass_SeesThePostEditHash_WithoutAReload and
#   P6b AnEarlierRunsSettledTask_StillHaltsOnDrift_WhenEditedAfterThisRunsLoad: the READ sites recompute
#       from disk today and must KEEP doing so. Section 11: "No task may pin the READ sites. Pinning R1
#       would make P1 pass and silence definition drift entirely - a strictly worse product than today."
#       These two are what make that implementation fail; they are green before and after by
#       construction. P6 OBVIOUS form was a tautology that passed with the read sites fully pinned -
#       read section 5.8 before touching either of them.
#   All three assert Expect='Executed' (present in the TRX, not [Skip]ped). They stay IN the manifest: a
#   dropped row and an oversight look identical from the outside.
#
#   THREE OF FOUR EXEMPT is high and it is deliberate: this file carries ONE defect pin and THREE
#   regression pins, which is exactly what section 5.8 asks of it. If a later edit wants a FOURTH
#   exemption, P2 has stopped being red - that is a finding about the tree, not a row to add.
#
# WHAT THIS CENSUS CANNOT SEE: it proves each test is COUPLED to the code path, never that its ASSERTION
#          is correct. A P2 that reaches the worktree settle and then asserts something hollow is red
#          here, green after, and passes. That residual stays a human read.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating (#455 companion (a)): no existing class in this project, and no other class this plan
# authors, contains 'MidRunDefinitionEditTests' as a substring. The nearest neighbour by subject is
# PlanEditedDuringRunTests, which does not contain it - and must NOT be selected here, because stage 2
# has deliberately left two of its assertions red until stages 5 and 13.
$filter = 'FullyQualifiedName~MidRunDefinitionEditTests'

# THE MANIFEST: each pin -> the test method name the ACTION PROMPT PINNED for it. A BARE STRING means
# Expect='Failed'. A HASHTABLE declares an EXEMPTION - a row a CORRECT implementation leaves GREEN on
# this tree, so demanding red would demand a correct implementation fail. See the header for each one.
$manifest = [ordered]@{
    'P2  the recorded hash is the PRE-EDIT pin (worktree, W2/W3)'   = 'TheRecordedHash_IsThePreEditPin_WhenTaskJsonIsEditedMidRun_Worktree'
    'P3  the trailer agrees with the journal on a real segment'     = @{ Name = 'TheTrailerAgreesWithTheJournal_OnARealGitSegment'; Expect = 'Executed' }
    'P6a the drift pre-pass sees the POST-edit hash, no reload'     = @{ Name = 'TheDriftPrePass_SeesThePostEditHash_WithoutAReload'; Expect = 'Executed' }
    'P6b an earlier run settled task still halts on drift'          = @{ Name = 'AnEarlierRunsSettledTask_StillHaltsOnDrift_WhenEditedAfterThisRunsLoad'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr32-midrun-census-$PID"
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
        $failures += "$pin -> '$name' is $seen on this tree, not Failed. Stage 4 pinned the SERIAL write sites and this tree still recomputes at the WORKTREE settle, so a correct P2 MUST fail here. If it passes, the assertion never reached the worktree settle path (Scheduler.SettleAsync is the DEFERRED settle and is the default for a real run), or it was weakened to something the defect satisfies. Assert EQUALITY against the value captured BEFORE the edit. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pins are not proven RED on this tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Census clean: all $($manifest.Count) pins are bound to a pinned test with the declared outcome (1 Failed, 3 declared-exempt Executed)."
exit 0
