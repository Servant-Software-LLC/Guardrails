# catches: a brownfield plan building on a RED base in its SECOND touched area - the existing
#          Integration tests that cover src/Guardrails.Cli/PlanPreflightPhase.cs, the file task 05
#          modifies, are already failing on the starting code. Task 04 inserts a new step into the
#          pre-DAG phase that every one of those tests drives; without this baseline, a red there is
#          ambiguous (broken by task 05 vs broken before the run started), the retry feedback blames
#          task 05, and the run burns its budget on a fault no work task can fix (#181). Re-emits the
#          failure DETAIL at the END so a red baseline's WHY reaches the halt feedback, not just
#          `[FAIL] <name>` (#179).
#
# WHY A SECOND PREFLIGHT AT ALL. The #181 baseline is deduped ONE PER TOUCHED AREA, not one per plan.
# This plan touches two test projects that already carry coverage:
#   tests/Guardrails.Core.Tests        - tasks 01/02 (see preflights/01-baseline-core-tests-green.ps1)
#   tests/Guardrails.Integration.Tests - task 05, which modifies PlanPreflightPhase.cs, and task 04 which writes its
#                                        wiring test into Samples/SampleVerifierWiringTests.cs
# Guardrails.Core.Tests references Guardrails.Core ONLY and cannot see PlanPreflightPhase at all, so
# preflight 01 says nothing whatsoever about this area. Two areas, two baselines.
#
# THE FILTER, AND WHY THIS ONE. Scope is the whole point of a preflight: the WHOLE Integration suite is
# 900 cases and SEVEN MINUTES (MEASURED 2026-08-29, --no-build: Failed 0, Passed 896, Skipped 4,
# Duration 7 m 11 s), and a pre-DAG phase is not the place to spend that. So the filter
# names the classes that actually cover the file task 05 modifies, derived mechanically rather than by
# taste - MEASURED 2026-08-29:
#
#   grep -rl "PlanPreflightPhase" over tests/Guardrails.Integration.Tests/**/*.cs
#     -> EXACTLY three source files: PlanPreflightPhaseTests.cs, PlanGuardrailPhaseTests.cs,
#        GateFailurePersistenceTests.cs.  (The other hits were bin/ and obj/ build output, excluded.)
#        Positive control for that zero-elsewhere claim (#500): the same invocation for a literal
#        known to be present, "PlanPreflightPhaseTests", returns 2 hits - so the search reached the
#        tree rather than silently skipping it.
#
# A Category trait cannot express that set: `Category=Preflights` is carried by only TWO of the three
# (PlanPreflightPhaseTests, PlanGuardrailPhaseTests) plus three unrelated classes
# (OverlappingWriteScopeAttributionTests, ReVerifierWiringTests, TaskPreflightSlotTests), and
# GateFailurePersistenceTests carries no Category trait at all - measured by grepping
# Trait("Category", ...) across the project. So the filter is a three-term class-name alternation.
#
#   dotnet test tests/Guardrails.Integration.Tests --no-build --nologo --list-tests --filter
#     "FullyQualifiedName~PlanPreflightPhaseTests|FullyQualifiedName~PlanGuardrailPhaseTests|FullyQualifiedName~GateFailurePersistenceTests"
#     -> 32 cases, and the full listing was read: 6 GateFailurePersistenceTests + 14 PlanGuardrailPhaseTests
#        + 12 PlanPreflightPhaseTests (six methods, two maxParallelism rows each). ZERO cases outside
#        those three classes - the substring terms are discriminating against every other class in the
#        900-case project.
#   Executing that same filter: Failed: 0, Passed: 32, Skipped: 0, Duration 2 m 33 s. NON-ZERO and
#   GREEN on the untouched tree, which is the check the zero-match guard below exists to keep true.
#
# NO `Category!=BacklogSlate` CONJUNCT, deliberately. The trait exists so a baseline can say "everything
# except the tests this plan is about to write"; here the class-name alternation already does that.
# Task 04's new class is SampleVerifierWiringTests, which shares no substring with any of the three
# terms, so it can never be selected - and adding a parenthesised conjunct would buy nothing while
# adding a filter-grammar failure mode to a check that halts the run before task one. Preflight 01
# still carries the `!=` form, because a whole-project filter there genuinely needs it.
#
# Worth-it gate (#181), all five held:
#   - the target pre-exists: 32 cases execute in these three classes on the untouched tree;
#   - the plan MODIFIES PlanPreflightPhase.cs, it does not create it (the file is present today);
#   - deterministic + cheap RELATIVE TO THE ALTERNATIVE: 32 of 900 cases, 2 m 33 s against 7 m 11 s for
#     the whole suite. No service boot, no network - these tests drive the real CLI over temp git repos;
#   - strictly narrower than the terminal gate, which runs this project UNFILTERED (guardrails/03);
#   - three work tasks reach this area: task 04 AUTHORS the wiring tests here and task 05 makes them
#     pass by editing PlanPreflightPhase.cs (they were one task until the TDD split - a single task
#     authoring both halves could not tell a wired phase from an unwired one, measured), and task
#     03's CommandFactory edit is compiled by this project (the only consumer of Guardrails.Cli).
#
# This check is POSITIVE / assert-present, so it is GREEN ON ARRIVAL by design (#479's named
# exception): a red here is a finding about the repo, not about this plan.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
$filter = 'FullyQualifiedName~PlanPreflightPhaseTests|FullyQualifiedName~PlanGuardrailPhaseTests|FullyQualifiedName~GateFailurePersistenceTests'
# NO --no-build here (unlike the authoring-time measurements above): the preflight runs against the
# STARTING repo, which may never have been built in this checkout, so it must build what it tests.
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
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
    Write-Output "the existing PlanPreflightPhase coverage in tests/Guardrails.Integration.Tests is already failing on the starting code - fix the pre-existing breakage before task 05 inserts a new step into that phase (#181)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean the baseline is green - a --filter that matches
# nothing, or is malformed, also exits 0, and a "baseline green" verdict certified by a run that
# executed nothing is the catalogue's vacuous baseline. A narrowed, hand-written three-term alternation
# is exactly where a typo lands, and this is the guard that catches it. Key on the EXECUTED count
# (Passed+Failed; "Total:" would also count [Skip]ped tests), never on "No test matches ..."
# (verbosity-dependent, #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. These three classes executed 32 tests under this exact filter when this plan was authored, so zero executed means a mistyped class name, a malformed alternation or a wrong project path - not that the area is empty."
    exit 1
}
exit 0
