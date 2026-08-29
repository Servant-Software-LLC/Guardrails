# catches: a brownfield plan building on a RED base in the area where THIS plan's risk actually
#          lives. The existing tests in tests/Guardrails.Integration.Tests are already failing on the
#          starting code, so a later task's tests-pass failure gets misattributed to that task, its
#          retry budget burns, and the run ends at needs-human with the deliverable complete.
#          Asserting them green BEFORE the DAG makes the attribution honest, and makes the TDD red of
#          tasks 01 and 04 unambiguous (#181). Re-emits the failure DETAIL at the END so a red
#          baseline's WHY reaches the halt feedback, not just `[FAIL] <name>` (#179).
#
# WHY THIS IS A GENUINE SECOND BASELINE AREA, and not a duplicate of preflights/01. Three of this
# plan's six tasks write tests into this project (01 authors LogSite/ServeDiagramTests.cs, 04 authors
# ModelTiering/ModelInRowTests.cs, 03 edits OnTheFlyDiagramTests.cs and
# RunCommandFinalSiteSettleTests.cs). More decisively, EVERY Guardrails.Cli file this plan modifies
# has its existing coverage HERE and ZERO in tests/Guardrails.Core.Tests - MEASURED 2026-08-29 over
# source .cs only, bin/obj excluded:
#   LogSiteRenderer.cs          Integration 6 files   Core 0
#   OnTheFlyDiagramObserver.cs  Integration 5 files   Core 0
#   ConsoleRunObserver.cs       Integration 8 files   Core 0
#   LiveRunObserver.cs          Integration 8 files   Core 0
#   LogServer.cs                Integration 2 files   Core 0
# (positive control for that census: `ModelTieringStage3`, a literal read out of the tree, returns 8
# Core.Tests files under the same invocation - so the zeros above are measurements, not a search that
# skipped its subject.) Preflights/01 cannot see any of that: it covers exactly one production file
# of this plan, HtmlDiagramRenderer.cs. Two areas, two baselines, deduped one-per-area.
#
# NO NARROW FILTER IS AVAILABLE HERE, and pretending otherwise would be the dishonest move. The four
# observers thread through EIGHTEEN distinct Integration.Tests classes (CollapseCompletedWavesTests,
# JitBreakdownVisibilityTests, LiveDisplayCollection, LogSiteExportTests, LogSiteHaltBannerTests,
# LogsCommandStatusTests, ModelTiering/AttemptModelForwardingTests, ModelTiering/
# AttemptModelRenderingTests, ModelTiering/ModelsUsedReportTests, NeedsHumanKindRenderingTests,
# OnTheFlyDiagramTests, OnTheFlyLogSiteTests, OverwatchNoVerdictRenderingTests,
# PostMortemLogsLinkTests, RateLimitedRenderingTests, RunCommandFinalSiteSettleTests,
# WaveCheckpointGraphTests, WaveGateForwardingTests). Any class-name filter narrow enough to be cheap
# would leave most of that surface unbaselined, so this runs the whole project minus the plan's own
# trait.
#
# COST - STATE IT PLAINLY SO NOBODY MISTAKES THIS FOR CHEAP. MEASURED 2026-08-29 on the untouched
# tree, THREE TIMES, and the SPREAD is the point: 900 tests (896 passed, 4 skipped) in 7 m 17 s and
# 7 m 41 s of test time on a quiet machine, and 14 m 28 s (880 s wall, this exact script, exit 0)
# when other builds were competing for the box. A sibling agent measured 7 m 11 s / 11 m 42 s on its
# machine. So budget 7-15 MINUTES, not "about six", and do NOT read a twelve-minute preflight as a
# hang. That is the price of the pre-DAG phase for this plan, and it is paid ONCE, before any task
# spends a token - which is the trade #181 is making. If that is too slow for a given run, the answer
# is to fix the suite's runtime, not to narrow this filter: the narrowing options are enumerated above
# and all of them leave the observer surface unbaselined.
#
# The `!=` exclusion is the ONE place the plan-wide trait stands ALONE (#455): "everything except the
# tests this plan is about to write". Bare, this trait is NOT a task-level selector - every task
# guardrail in this plan conjoins its OWN test class beside it (tasks 01/02 conjoin
# ~ServeDiagramTests, tasks 04/05 conjoin ~ModelInRowTests).
#
# MEASURED, not recalled (#248) - the two halves of the `!=` semantics, run against THIS project and
# runner at authoring time (2026-08-29). The zero-match guard below is NOT optional and this is why:
#   FullyQualifiedName~LogServerTests                                    -> executed 35, exit 0
#   FullyQualifiedName~LogServerTests&Category!=BacklogSlate             -> executed 35, exit 0
#     => `!=` INCLUDES tests carrying no Category trait. Every existing test in this project is one of
#        those (`git grep -c BacklogSlate -- src tests` exits 1 with no output on the untouched tree),
#        so this filter does NOT vacuously select zero and red-halt before the DAG.
#   FullyQualifiedName~AttemptModelRenderingTests                        -> executed  4, exit 0
#   FullyQualifiedName~AttemptModelRenderingTests&Category!=ModelTieringStage3
#                                                                        -> executed  0, "No test matches", exit 0
#     => `!=` DOES exclude a traited test, AND a zero match exits 0. A guard keyed on anything but the
#        executed count would let that pass as "baseline green".
#   Keyed on Passed+Failed, never "Total:" - this suite genuinely has 4 [Skip]ped tests, so a
#   Total:-keyed guard would count them as evidence the suite ran.
#
# RECENT HISTORY WORTH KNOWING WHEN THIS GOES RED. This suite had a real PROCESS-WIDE flake fixed at
# commit b43232d: Spectre's AnsiConsole.Live exclusivity lock is process-wide, so two live-display
# tests running in parallel threw from DisposeAsync and the failure was attributed to whichever test
# happened to be tearing down. That fix added LiveDisplayCollection.cs and serialised
# CollapseCompletedWavesTests and JitBreakdownVisibilityTests. That is exactly the class of
# pre-existing red this baseline exists to attribute correctly - to the repo, before the DAG, rather
# than to whichever task's guardrail met it first.
#
# A SECOND, LIVE-MEASURED FAILURE SHAPE, because it will otherwise cost an operator an hour. While
# authoring this file the suite exited 1 with "Failed: 0, Passed: 896". The cause was NOT a failing
# test: HostRepoCleanlinessGuard (an IClassFixture on RetrySalvageTests, #253) asserts in its Dispose
# that `git status --porcelain` of the ENCLOSING checkout gained no new line during the class, and a
# file had been created in the repo WHILE the suite ran. It surfaces only as
#   [Test Class Cleanup Failure (...RetrySalvageTests)] Xunit.Sdk.TestPipelineException
# with no message text at all. Re-run on a quiescent tree: 13/13 passed, exit 0, porcelain count
# stable. The guard's own #433 comment predicts this and carves out only the LINKED-worktree case
# (.git as a FILE); a normal checkout (.git as a DIRECTORY) keeps the tripwire armed - and a plan-root
# preflight runs BEFORE the DAG, so nothing of the harness's own should be mutating the tree then.
# The Failed:-0 branch below names this shape rather than sending the operator hunting for a broken
# test. It does NOT weaken the check: the exit code is still 1.
#
# This check is POSITIVE / assert-present, so it is GREEN ON ARRIVAL by design (#479's named
# exception): a red here is a finding about the repo, not about this plan.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guards below read is LOCALIZED (#455)
$filter = 'Category!=BacklogSlate'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)
$joined = $out | Out-String

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:|Cleanup Failure' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the halt feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }

    $failedCount = [regex]::Match($joined, 'Failed:\s*(\d+)')
    if ($failedCount.Success -and [int]$failedCount.Groups[1].Value -eq 0) {
        Write-Output ""
        Write-Output "NOTE: the runner reports Failed: 0 yet exited $testExit. That is the CLASS-CLEANUP shape, not a failing assertion - look for '[Test Class Cleanup Failure (...)]' above. The known instance is HostRepoCleanlinessGuard on RetrySalvageTests (#253/#433): it fails when the enclosing git checkout gains an untracked or modified path WHILE the suite runs. Check whether something wrote into the repo during this preflight (an editor, a concurrent build, another agent) and re-run on a quiescent tree before concluding the area is red."
    }

    Write-Output "the existing tests in tests/Guardrails.Integration.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181). This baseline executed 896 tests green (900 total, 4 skipped) when the plan was authored. This is the suite that carries ALL existing coverage for LogSiteRenderer.cs, OnTheFlyDiagramObserver.cs, LogServer.cs, LiveRunObserver.cs and ConsoleRunObserver.cs, so a red here means every observer task in this plan would have been building on an unknown base."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean the baseline is green - a --filter that matches
# nothing, or is malformed, also exits 0 (measured above), and a "baseline green" verdict certified by
# a run that executed nothing is the catalogue's vacuous baseline. Key on the EXECUTED count
# (Passed+Failed; "Total:" would also count this suite's 4 [Skip]ped tests), never on
# "No test matches ..." (verbosity-dependent, #248).
$ran = ([regex]::Matches($joined, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. tests/Guardrails.Integration.Tests executed 896 tests under this exact filter when the plan was authored, so zero executed means the filter or the project path is wrong, not that the area is empty."
    exit 1
}
exit 0
