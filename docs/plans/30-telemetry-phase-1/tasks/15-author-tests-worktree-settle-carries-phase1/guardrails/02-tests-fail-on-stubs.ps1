# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          about a PendingAttempt the test itself constructed, any assertion that never obtains one from
#          AttemptJournaler.ValidateFragmentForSettle). It PASSES on the pre-implementation tree and hides
#          behind its genuinely-failing siblings, so a suite-level non-zero exit would certify the file
#          honest (#375). One entry per enumerated behaviour, each observed Failed in the runner's OWN
#          TRX - never merely discovered by name, which a hollow body satisfies exactly as a comment
#          satisfies a token floor.
#
#          "on stubs" is this plan's file name for the pre-implementation tree. THIS task writes no stub:
#          every member it asserts on already exists (tasks 03 and 04 declared them) and simply nobody
#          SETS it on the worktree path, so the red is a RUNTIME red. That is a weaker red than a
#          throwing stub, which is precisely why the census below is per-test rather than suite-level:
#          with a throwing stub every honest test is red for free, and here each one has to earn it.
#
# CENSUS FORM, and it is a DELIBERATE ASYMMETRY with tasks 03, 04 and 04a - recorded here so a reader
#          comparing the two does not read it as an oversight. Those three run a FORWARD per-test census
#          (each enumerated behaviour must be found in the runner's own TRX observed 'Passed'). This one
#          is the RED per-test census: same family, opposite polarity, because at THIS point in the pair
#          a 'Passed' row IS the failure - the implementation does not exist yet. There is no forward
#          per-test census anywhere in this pair: the forward half for these four behaviours is task 16's
#          02-worktree-settle-tests-pass.ps1, which is deliberately SUITE-LEVEL rather than per-test, and
#          which states its own reason in its header. Read the two headers together before changing
#          either.
#
# NO EXEMPTIONS, and the fourth row is the one that needed the argument. All four behaviours assert that
#          a Phase-1 carrier is populated on a tree where nothing populates any of them, so every honest
#          test here is red. The fourth -
#          'EveryPhase1AttemptMemberSetOnTheSerialRecord_IsAlsoSetOnTheWorktreeRecord' - is red ONLY IF it
#          is written as a two-sided assertion. Its NAME reads like an implication, and the implication
#          form is VACUOUSLY TRUE here: neither settle path sets anything yet, so "for every member set on
#          the serial record..." quantifies over an empty set and the test is green while asserting
#          nothing at all. A green row here is therefore not "the feature already works" - it is the
#          hollow form of the single most valuable test in this pair, and the failure text below says so.
#
# WHY THESE ARE TESTS AND NOT A SOURCE-SHAPE CHECK (the #468 demotion order, worked): the property
#          "ValidateFragmentForSettle populates the carrier" is observable at RUNTIME - AttemptJournaler
#          is internal sealed and Core.Tests has InternalsVisibleTo, so the object it builds can simply be
#          inspected. Rung 1 applies and a test carries it. Only the SECOND half of the datum's journey -
#          that Scheduler.RecordSucceededSettle's own AttemptRecord initializer READS those carriers -
#          resists a test, because observing it means driving the whole scheduler through a real worktree
#          provider. That half is task 16's 03-both-settle-records-set-every-phase1-member.ps1, one of
#          only two source-shape guardrails in this plan. These tests are the FIRST line of defence.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The four names below were read side by side with this task's
#          action.prompt.md table, which pins each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
#          it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test class, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 - the class term alone. 'WorktreeSettlePhase1Tests' was checked against all 195
# existing Core test class names and every other class this plan authors: it is a substring of none of
# them, and none of them is a substring of it (the nearest neighbour, Phase1TelemetryRowTests, shares
# only the 'Phase1' fragment and neither name contains the other), so the filter is discriminating.
$filter = 'FullyQualifiedName~WorktreeSettlePhase1Tests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'the worktree PendingAttempt carries the bucket'          = 'TheWorktreePendingAttempt_CarriesTheBucket'
    'the worktree PendingAttempt carries the turn count'      = 'TheWorktreePendingAttempt_CarriesTheTurnCount'
    'the worktree PendingAttempt carries the segments'        = 'TheWorktreePendingAttempt_CarriesTheSegments'
    'both settle paths set every Phase-1 attempt member'      = 'EveryPhase1AttemptMemberSetOnTheSerialRecord_IsAlsoSetOnTheWorktreeRecord'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

$out = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose THAT. Falling
# through would print "every behaviour unbound", a confident wrong message aimed at the one artifact a
# retry agent is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
# navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make the guard below
# evaluate 1 -lt 1 and NEVER FIRE.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -lt 1) {
        continue
    }
    $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
    if ($name -eq 'EveryPhase1AttemptMemberSetOnTheSerialRecord_IsAlsoSetOnTheWorktreeRecord') {
        $failures += "$behaviour -> '$name' is $seen on the pre-implementation tree, not Failed. This is almost certainly the VACUOUS IMPLICATION: nothing sets a Phase-1 member on EITHER settle path yet, so 'for every member set on the serial record, assert it is also set on the worktree record' quantifies over an empty set and passes while asserting nothing. Write the TWO-SIDED assertion instead - for each of the three NAMED Phase-1 carriers (PendingAttempt.Turns, .Segments, .Bucket), assert the serial side carries a non-null value AND the worktree side carries one, taking the counterpart from Journal.AttemptRecord for Turns and Segments and from Journal.TaskJournalEntry for Bucket. Name the three as ordinary member access; do NOT try to discover them by reflection, since nothing marks a member as a Phase-1 carrier. ('NotExecuted' = [Fact(Skip=...)].)"
    }
    else {
        $failures += "$behaviour -> '$name' is $seen on the pre-implementation tree, not Failed. Nothing sets PendingAttempt.Bucket, .Turns or .Segments yet, so a test that OBTAINS its PendingAttempt from AttemptJournaler.ValidateFragmentForSettle and asserts the carrier is populated cannot pass. Green here means the test never obtained one - most likely it constructed a PendingAttempt itself and asserted about the object it just built, which passes today and forever. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the pre-implementation tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Worktree is the DEFAULT execution mode, and JournalModel.cs already documents the failure this pair guards (grep 'A member hung directly off the attempt record'). A hollow test here does not merely fail to help - it certifies the exact silent-vanish defect as covered."
    exit 1
}

Write-Output "Per-test red census: all $($manifest.Count) enumerated behaviours are bound to a pinned test observed Failed on the pre-implementation tree."
exit 0
