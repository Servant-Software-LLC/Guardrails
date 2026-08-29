# catches: a green-per-task plan that broke something it never looked at, in the ONE suite where this
#          plan's risk actually lives. Every task's own filtered tests passed, and the union regressed
#          a neighbouring observer behaviour that no task-level --filter selected. It is also the
#          second half of the #181 baseline for this area: green START before the DAG
#          (preflights/02), green END on the merged HEAD (here). Re-emits the assertion/exception
#          lines at the END so a red terminal gate's WHY reaches the operator, not just
#          `[FAIL] <name>` (#179).
#
# THIS IS THE MOST LOAD-BEARING GATE IN THE PLAN, and the measurement says why. FIVE of the nine
# production files this plan modifies have their ENTIRE existing coverage in this suite and ZERO in
# tests/Guardrails.Core.Tests - MEASURED 2026-08-29 over source .cs only, bin/obj excluded:
#   src/Guardrails.Cli/Ui/LogSiteRenderer.cs          Integration 6 files   Core 0
#   src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs  Integration 5 files   Core 0
#   src/Guardrails.Cli/ConsoleRunObserver.cs          Integration 8 files   Core 0
#   src/Guardrails.Cli/Ui/LiveRunObserver.cs          Integration 8 files   Core 0
#   src/Guardrails.Cli/Ui/LogServer.cs                Integration 2 files   Core 0
#   src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs  Integration 7 files   Core 0
# (positive control for that census: a literal read out of the tree, `ModelTieringStage3`, returns
# 4 Core.Tests files - so a zero above is a measurement, not a search that never opened the door.
# RE-MEASURED 2026-08-29 with `git grep -l ModelTieringStage3 -- tests/Guardrails.Core.Tests`: the
# figure previously written here was 8 and it was wrong. The control still does its job - a nonzero
# proves the search reached the project - but the number is now the one the command prints.)
# Eighteen distinct Integration.Tests classes touch those observers, and task 04's contract change
# adds two more that exist ONLY here - AttemptModelDisclosureTests and AttemptModelForwardingTests,
# the pair that pins the attempt-model raise counts and the decorator forwarding a new default
# interface member can silently break. On top of that, task 03 is
# deliberately licensed to EDIT two EXISTING files in this suite - OnTheFlyDiagramTests.cs (10
# [Fact]/[Theory]) and RunCommandFinalSiteSettleTests.cs (2) - to retire the assertions #523 makes
# false. Nothing but a whole-suite run can see an over-aggressive retirement, because every
# task-level filter in this plan names a class this plan AUTHORS.
#
# scope: LOCAL (no sidecar `scope` key), deliberately, and this is the decision most worth not
# reversing. A whole-suite run is a TERMINAL POSTCONDITION, not an integration check. Tagging it
# scope:"integration" would re-run it at EVERY union point, including partial merges where a
# downstream TDD task has not run yet - and this plan has two author-tests tasks (01 and 05) whose
# tests are INTENTIONALLY RED until their implementing sibling lands, in THIS suite. A
# scope:"integration" tag here would red-halt a correct run at task 01's own merge. That is the #125
# anti-pattern with a live example attached. The union-safe integration invariant this plan does
# carry is 04-union-artifacts-sound.ps1, which is conditional throughout.
#
# COST, MEASURED 2026-08-29 on the untouched tree, THREE TIMES, and the SPREAD is the number that
# matters: 900 tests in 7 m 17 s (this exact script, 445 s wall, exit 0), 7 m 41 s, and 14 m 28 s when
# other builds were competing for the box. A sibling agent measured 7 m 11 s / 11 m 42 s on its
# machine. So budget 7-15 MINUTES and do not read a twelve-minute gate as a hang. This is minutes, not
# seconds - which is exactly why it is a terminal gate and not a per-task check.
#
# NO --filter here: the #455 rule governs TASK-LEVEL filters, whose job is to name the pair's own
# class. This is the terminal whole-suite gate, the one place "all tests pass" belongs.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guards below read is LOCALIZED (#455)
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --nologo 2>&1
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
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }

    # NON-ZERO EXIT WITH `Failed: 0` IS A DISTINCT, KNOWN SHAPE - name it, do not let an operator hunt
    # for a broken test that does not exist. MEASURED 2026-08-29 while authoring this file: this suite
    # exited 1 with "Failed: 0, Passed: 896" because HostRepoCleanlinessGuard (an IClassFixture on
    # RetrySalvageTests, issue #253) asserts in its Dispose that `git status --porcelain` of the
    # enclosing checkout gained no NEW line during the class - and a file was created in the repo
    # WHILE the suite ran. It surfaces only as
    #   [Test Class Cleanup Failure (...RetrySalvageTests)] Xunit.Sdk.TestPipelineException
    # with no message text at all. Re-running the class on a quiescent tree exited 0 (13/13 passed,
    # porcelain count stable at 56 before and after), so it is NOT a broken test. The guard's own #433
    # comment predicts this exactly and carves out only the LINKED-worktree case (.git as a FILE); a
    # normal checkout (.git as a DIRECTORY) keeps the tripwire armed. This branch does NOT weaken the
    # gate - it still exits 1 - it makes the failure ACTIONABLE, which is the whole point of #179.
    $failedCount = [regex]::Match($joined, 'Failed:\s*(\d+)')
    if ($failedCount.Success -and [int]$failedCount.Groups[1].Value -eq 0) {
        Write-Output ""
        Write-Output "NOTE: the runner reports Failed: 0 yet exited $testExit. That is the CLASS-CLEANUP shape, not a failing assertion - look for '[Test Class Cleanup Failure (...)]' above. The known instance here is HostRepoCleanlinessGuard on RetrySalvageTests (#253/#433): it fails when the enclosing git checkout gains an untracked/modified path WHILE the suite runs. Check whether something wrote into the repo during this gate (an editor, a concurrent build, another agent) and re-run on a quiescent tree before concluding the suite is red."
    }

    Write-Output "the Guardrails.Integration test suite is RED on the merged plan-branch HEAD - the union of this plan's tasks regressed something (see failure details above). This is the suite that carries ALL existing coverage for LogSiteRenderer.cs, OnTheFlyDiagramObserver.cs, OnTheFlyLogSiteObserver.cs, LogServer.cs, LiveRunObserver.cs and ConsoleRunObserver.cs, so start there. Two specific suspects in this plan: task 03, which is licensed to retire assertions from OnTheFlyDiagramTests.cs and RunCommandFinalSiteSettleTests.cs - check it retired no more than #523 actually made false; and task 04, which adds a member to the PUBLIC IRunObserver interface and forwards it from both decorators - AttemptModelDisclosureTests and AttemptModelForwardingTests are the two classes that see a raise moved, duplicated or unforwarded."
    exit 1
}

# ZERO-MATCH GUARD (#455): a test host that started, executed nothing and exited 0 would otherwise
# certify the whole suite green over an empty run. Key on the EXECUTED count (Passed+Failed; "Total:"
# would also count [Skip]ped tests - this suite HAS 4 skipped tests, so keying on Total: here would
# be measurably wrong), never on a verbosity-dependent string (#248).
$ran = ([regex]::Matches($joined, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this terminal gate certified nothing. tests/Guardrails.Integration.Tests executed 896 tests (900 total, 4 skipped) when this guardrail was authored, so zero executed means the project path is wrong or the test host never started, not that the suite is empty."
    exit 1
}
exit 0
