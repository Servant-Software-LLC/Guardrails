# catches: a brownfield plan building on a RED base - the EXISTING tests in tests/Guardrails.Core.Tests,
#          the area this plan's diagram-refresh deliverable adds a test into and edits a test in, are
#          already failing on the starting code. Asserting them green BEFORE the DAG means a later
#          task's tests-pass failure is attributable to THAT task rather than to pre-existing
#          breakage, and the new tests' TDD red is unambiguous (#181). Re-emits the failure DETAIL at
#          the END so a red baseline's WHY reaches the halt feedback, not just `[FAIL] <name>` (#179).
#
# WHAT THIS PLAN ACTUALLY DOES IN THIS AREA - stated truthfully, because the number matters. This plan
# authors exactly ONE file in tests/Guardrails.Core.Tests: Graph/DiagramRefreshTests.cs (task 03,
# #523). It EDITS one existing file there: HtmlDiagramRendererTests.cs, and only to retire the
# assertions #523 makes false. It writes no other TEST into this project - but it does modify two
# more Guardrails.Core PRODUCTION files, IRunObserver.cs and TaskExecutor.cs (task 04, #524), whose
# implementors include seven IRunObserver types living in THIS test project. That makes this
# baseline load-bearing for the contract change too, not only for the diagram renderer. The
# remaining production files this plan modifies are in Guardrails.Cli, whose coverage is entirely
# in tests/Guardrails.Integration.Tests (see preflights/02, the second baseline area). One area here,
# one file authored, one file edited; the per-area dedupe therefore yields exactly this file and
# preflights/02, and no third.
#
# Scope: the AREA (this one test project), never the whole solution - a whole-solution test run hits
# the #165/#176 compile-coupling trap on a mid-TDD tree and dead-ends a run no work task can rescue.
#
# The `!=` exclusion is the ONE place the plan-wide trait stands ALONE (#455). It exists so this
# preflight can say "everything except the tests this plan is about to write": the pre-DAG phase runs
# against the STARTING bytes, where none of the BacklogSlate tests exist yet, and the filter makes
# that intent explicit and robust. Bare, this trait is NOT a task-level selector - every task
# guardrail in this plan that owns a BacklogSlate class conjoins that class beside it (task 03
# conjoins ~DiagramRefreshTests). Task 04, the contract task, owns NO BacklogSlate class: it authors
# no tests, and its regression guard names two pre-existing classes
# (AttemptModelDisclosureTests / AttemptModelForwardingTests) with no Category term at all.
#
# MEASURED, not recalled (#248) - the two halves of the `!=` semantics this preflight depends on, run
# against this exact project and runner at authoring time (2026-08-29):
#   FullyQualifiedName~HtmlDiagramRendererTests                              -> executed 48, exit 0
#   FullyQualifiedName~HtmlDiagramRendererTests&Category!=BacklogSlate       -> executed 48, exit 0
#     => `!=` INCLUDES tests that carry no Category trait at all. Every existing test in this project
#        is one of those (`git grep -c BacklogSlate -- src tests` exits 1 with no output on the
#        untouched tree; the same invocation for a literal that IS present, ModelTieringStage3,
#        returns 6 files across src and tests - so that zero is a measurement, not a search that
#        skipped its subject; RE-MEASURED 2026-08-29, the figure previously written here was 8), so
#        this filter does NOT vacuously select zero and red-halt before the DAG.
#   FullyQualifiedName~TierClassificationAuditTests                          -> executed 12, exit 0
#   FullyQualifiedName~TierClassificationAuditTests&Category!=ModelTieringStage3
#                                                                            -> executed  0, "No test matches", exit 0
#     => `!=` DOES exclude a test carrying the named trait. So once the BacklogSlate tests exist, this
#        preflight will genuinely skip them rather than merely appearing to - and a zero match exits
#        0, which is why the executed-count guard below is not optional.
#
# COST, MEASURED 2026-08-29 on the untouched tree: this filter executed 2006 tests in 46 s (49 s wall,
# prebuilt), exit 0. Seconds-to-a-minute, not minutes - unlike preflights/02.
#
# This check is POSITIVE / assert-present, so it is GREEN ON ARRIVAL by design (#479's named
# exception): a red here is a finding about the repo, not about this plan.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
$filter = 'Category!=BacklogSlate'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
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
    Write-Output "the existing tests in tests/Guardrails.Core.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181). This baseline executed 2006 tests green when the plan was authored, so a red here is a change in the repo, not in the plan."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean the baseline is green - a --filter that matches
# nothing, or is malformed, also exits 0 (measured above: the TierClassificationAuditTests probe
# executed zero tests and exited 0), and a "baseline green" verdict certified by a run that executed
# nothing is the catalogue's vacuous baseline. Key on the EXECUTED count (Passed+Failed; "Total:"
# would also count [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. tests/Guardrails.Core.Tests executed 2006 tests under this exact filter when the plan was authored, so zero executed means the filter or the project path is wrong, not that the area is empty."
    exit 1
}
exit 0
