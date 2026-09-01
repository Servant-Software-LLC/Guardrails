# catches: a milestone-C pin that grades the WRONG MECHANISM. Section 6.7 is blunt about it: milestone C
#          is fully satisfiable without ever consulting the load-time pin - drive the divergence flag
#          from LivePlanEditWatch already-collected edits and P9 through P13 ALL PASS, shipping the
#          watch MOVING baseline under this plan's name. And asserting the report's payload is not
#          enough, because a watch-driven implementation can populate both hash fields from the watch's
#          own before/after snapshot and satisfy a payload pin exactly.
#
#          P15 is therefore written to discriminate on PROVENANCE, and this census is what proves it was:
#          after a mid-run edit that Poll() has ALREADY reported and re-baselined on - so the watch holds
#          the POST-edit bytes and will never report that file again - the settling task must STILL
#          diverge. Only a pinned baseline survives that. If P15 is green on this tree it is not testing
#          provenance, because nothing on this tree diverges at all.
#
# TWO DECLARED EXEMPTIONS - P10 and P16 - and both are SILENCE pins, which is why they are green:
#   P10 AnUneditedRun_WritesNoDivergenceKeyAndNoDivergenceDecision asserts on the FULL decisions list and
#       the FULL run.json key set, not on the absence of one token. Plan 31 section 8's lesson: a silence
#       pin that checks one token passes trivially when the mechanism is broken. Today nothing emits the
#       key or the decision, so a CORRECT test is green; its job is to STAY green after stage 13, which
#       is Risk 3's only mitigation ("a defect in HasExecutedDefinitionDivergence silently stops the
#       product delivering anything").
#   P16 AStrayEditorArtifactMidRun_LeavesTheRunGreenAndDelivering is section 6.2's tripwire at Core
#       level: a stray editor artifact must leave the run GREEN AND DELIVERING while the RECORDED hash
#       still differs from disk. A whole-surface gate turns it red, and section 6.2 calls a gate that
#       blocks delivery on an editor artifact "disabled within a week, and then the real signal is gone
#       too".
#   P16b APreExistingEditorArtifact_LeavesTheRunGreenAndDelivering is the OTHER SIDE of that tripwire,
#       and it exists because P16 alone cannot see the reachable half. P16's artifact appears MID-RUN, so
#       it is absent from the load-time map and present in the settle walk - an implementation that
#       filters ONLY the settle side still passes it. An artifact present AT LOAD is the case that bites:
#       filtered on one side only, its label is in BEFORE and not in AFTER, reads as VANISHED, and blocks
#       delivery on a run nobody edited. A .DS_Store already in the checkout, an operator's .swp, a
#       .orig/.rej from any pre-run git operation - all reachable, none requiring anyone to touch the
#       plan folder at all. Green today, green after, declared exemption for the same reason P16 is.
#
#   THREE OF FIVE EXEMPT now, and the ratio is worth defending rather than shrugging at: this file
#       carries TWO defect pins (P12, P15) and THREE silence/regression pins (P10, P16, P16b). A silence
#       pin is green on both sides by definition - that IS its content, not a weakness in it. If a later
#       edit wants a FOURTH exemption, re-read section 6.7 before adding it.
#   Both assert Expect='Executed' (present in the TRX, not [Skip]ped). They stay IN the manifest: a
#   dropped row and an oversight look identical from the outside.
#
# P12 IS ONE PIN, TWO-SIDED - do NOT split it into five negatives. Section 6.7 does the reachability
#          analysis once, by hand, precisely so an unattended agent is not handed the instruction "each
#          must test a reachable state", which no guardrail can check: all five harness writers act at
#          WAVE BOUNDARIES and none can execute between a task's dispatch and that task's settle within a
#          wave, so five negative pins would be five vacuous tests. The firing half is what makes the
#          silent half worth asserting; a one-sided silence pin is satisfied by a gate that never fires.
#
# WHAT THIS CENSUS CANNOT SEE: it proves each test is COUPLED to the gate, never that its assertion is
#          correct. P15 is where that residual matters most - a test that reaches the report and then
#          asserts something hollow is red here, green after, and passes - and P15 is the pin standing
#          between this plan and a differently-named reimplementation of the plan-edit watch.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating (#455 companion (a)): no existing class in this project, and no other class this plan
# authors, contains 'ExecutedDefinitionDivergenceTests' as a substring. In particular it does NOT
# contain 'ExecutedDefinitionHashTests', so stage 1's file is not swept in.
$filter = 'FullyQualifiedName~ExecutedDefinitionDivergenceTests'

# THE MANIFEST: each pin -> the test method name the ACTION PROMPT PINNED for it. A BARE STRING means
# Expect='Failed'. A HASHTABLE declares an EXEMPTION - a row a CORRECT implementation leaves GREEN on
# this tree, so demanding red would demand a correct implementation fail. See the header for each one.
$manifest = [ordered]@{
    'P12 the JIT breakdown pin, two-sided (in-wave silent, out-of-wave FIRES)' = 'AJitBreakdownWritingOutsideItsWave_Diverges_WhileOneInsideItIsSilent'
    'P15 the gate reads the PIN, not the watch moving baseline'               = 'ADivergenceIsReported_EvenAfterTheWatchAlreadyReportedAndReBaselined'
    'P10 an unedited run gains no key and no decision (full list)'            = @{ Name = 'AnUneditedRun_WritesNoDivergenceKeyAndNoDivergenceDecision'; Expect = 'Executed' }
    'P16 the gate is QUIETER than the recorded hash'                          = @{ Name = 'AStrayEditorArtifactMidRun_LeavesTheRunGreenAndDelivering'; Expect = 'Executed' }
    'P16b the gate filters the LOAD side too, not just the settle walk'      = @{ Name = 'APreExistingEditorArtifact_LeavesTheRunGreenAndDelivering'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr32-divergence-census-$PID"
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
        $failures += "$pin -> '$name' is $seen on this tree, not Failed. There is no divergence gate on this tree at all - stage 13 builds it - so a correct pin MUST fail here. If it passes, the assertion never reached the gate: P12's FIRING half and P15's provenance discriminator both require an ExecutedDefinitionDivergence to be reported, and nothing reports one yet. A pin that is green now will be green forever. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pins are not proven RED on this tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Census clean: all $($manifest.Count) pins are bound to a pinned test with the declared outcome (2 Failed, 3 declared-exempt Executed)."
exit 0
