# catches: a green-per-task plan that broke something it never looked at - every task's own filtered
#          tests passed, and the union regressed a neighbouring behaviour in Guardrails.Core that no
#          task-level --filter selected. It is also the second half of the #181 baseline: green START
#          before the DAG (preflights/01), green END on the merged HEAD (here). Re-emits the
#          assertion/exception lines at the END so a red terminal gate's WHY reaches the operator,
#          not just `[FAIL] <name>` (#179).
#
# WHY THIS GATE IS WORTH ITS MINUTE, stated accurately for THIS plan. Of the nine production files
# this plan modifies, THREE live in Guardrails.Core: src/Guardrails.Core/Graph/
# HtmlDiagramRenderer.cs (task 03, #523) and, since the plan grew its contract task,
# src/Guardrails.Core/Execution/IRunObserver.cs and src/Guardrails.Core/Execution/TaskExecutor.cs
# (task 04, #524). The last two are why this gate matters MORE than it used to: THIRTY types
# implement IRunObserver across src/ and tests/, and SEVEN of them live in
# tests/Guardrails.Core.Tests (EscalationSinkTests, OverwatchNoVerdictTests,
# SchedulerBreakdownPhaseEventsTests, SchedulerDriftAutoResolveTests, TopologyM2CleanupTests and
# more) - a suite no task-level filter in this plan selects.
# The diagram renderer is the other half of the story. That one file has substantial existing coverage OUTSIDE the
# plan's Category=BacklogSlate trait - MEASURED 2026-08-29, THREE Core.Tests classes reference
# HtmlDiagramRenderer (HtmlDiagramRendererTests, GraphSourceHashTests, MermaidRendererTests) carrying
# 100 [Fact]/[Theory] declarations between them, and `--filter FullyQualifiedName~
# HtmlDiagramRendererTests` alone EXECUTES 48 tests. None of them carries a Category trait, so no
# task-level filter in this plan selects any of them.
#
# And task 03 is explicitly licensed to EDIT tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs -
# only to retire the assertions #523 makes false. This whole-suite gate is the only check that can
# see an over-aggressive retirement: a task that deletes more than the meta-refresh assertions still
# passes its own narrowed --filter, because that filter names DiagramRefreshTests, not this file.
#
# WHAT THIS GATE DOES **NOT** COVER, so nobody mistakes it for the whole story: the other six files
# this plan touches are all in Guardrails.Cli, and MEASURED on the untouched tree their Core.Tests
# coverage is EXACTLY ZERO (LogSiteRenderer 0 files, OnTheFlyDiagramObserver 0, LogServer 0,
# LiveRunObserver 0, ConsoleRunObserver 0, OnTheFlyLogSiteObserver 0; every one of them is covered
# only in tests/Guardrails.Integration.Tests). That is what 03-integration-suite-passes.ps1 is for. Neither
# gate substitutes for the other.
#
# scope: LOCAL (no sidecar `scope` key), deliberately. A whole-suite run is a TERMINAL POSTCONDITION.
# Tagging it scope:"integration" would re-run it at every union point, on partial merges where a
# downstream TDD task has not run yet - so the deliberately-red author-tests of tasks 01 and 05 would
# red-halt a correct run. That is the #125 anti-pattern by name.
#
# NO --filter here, and that is not an oversight: the #455 rule governs TASK-LEVEL filters, whose job
# is to name the pair's own class. This is the terminal whole-suite gate, the one place "all tests
# pass" belongs, so there is nothing to scope it to.
#
# Cost, MEASURED 2026-08-29 on the untouched tree: `dotnet test tests/Guardrails.Core.Tests
# --filter Category!=BacklogSlate` executed 2006 tests in 46 s (49 s wall, prebuilt). Unfiltered it
# is the same set plus whatever this plan adds. Budget about a minute, not seconds.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Guardrails.Core test suite is RED on the merged plan-branch HEAD - the union of this plan's tasks regressed something (see failure details above). There are exactly TWO likely causes in THIS plan, because exactly two of its tasks touch Guardrails.Core. (1) Task 03 edits src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs and is licensed to retire assertions from tests/Guardrails.Core.Tests/HtmlDiagramRendererTests.cs - check whether it retired more than the meta-refresh assertions #523 actually made false, and check GraphSourceHashTests and MermaidRendererTests, which also pin that renderer's output. (2) Task 04 adds a member to the PUBLIC IRunObserver interface and raises it from TaskExecutor - SEVEN IRunObserver implementations live in this very project (EscalationSinkTests, OverwatchNoVerdictTests, SchedulerBreakdownPhaseEventsTests, SchedulerDriftAutoResolveTests, TopologyM2CleanupTests and more), and any of them stops compiling if that member lost its default no-op body."
    exit 1
}

# ZERO-MATCH GUARD (#455): a test host that started, executed nothing and exited 0 would otherwise
# certify the whole suite green over an empty run. Key on the EXECUTED count (Passed+Failed; "Total:"
# would also count [Skip]ped tests), never on a verbosity-dependent string (#248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this terminal gate certified nothing. tests/Guardrails.Core.Tests executed 2006 tests when this guardrail was authored, so zero executed means the project path is wrong or the test host never started, not that the suite is empty."
    exit 1
}
exit 0
