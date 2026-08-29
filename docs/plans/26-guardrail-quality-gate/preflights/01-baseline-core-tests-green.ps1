# catches: a brownfield plan building on a RED base - the EXISTING tests in tests/Guardrails.Core.Tests,
#          the area this plan's first two tasks add to, are already failing on the starting code.
#          Asserting them green BEFORE the DAG means a later task's tests-pass failure is attributable
#          to THAT task rather than to pre-existing breakage, and the new tests' TDD red is
#          unambiguous (#181). Re-emits the failure DETAIL at the END so a red baseline's WHY reaches
#          the halt feedback, not just `[FAIL] <name>` (#179).
#
# Scope: the AREA (this one test project), never the whole solution - a whole-solution test run hits
# the #165/#176 compile-coupling trap on a mid-TDD tree and dead-ends a run no work task can rescue.
#
# This plan authors exactly ONE file in this area: tests/Guardrails.Core.Tests/Samples/SampleVerifierTests.cs
# (task 01's writeScope; task 02 implements against it without writing it). That single file is the
# whole of this plan's Core-side test surface - there is no second subfolder here and no second Core
# test file. The per-area dedupe therefore yields exactly this one file for the Core area, and one
# more for the OTHER touched area with pre-existing coverage (tests/Guardrails.Integration.Tests,
# where task 04 authors the wiring tests and task 05 modifies PlanPreflightPhase.cs) - see preflights/02-baseline-preflight-phase-tests-green.ps1.
#
# Worth-it gate (#181), all five held and each MEASURED on 2026-08-29:
#   - the target pre-exists: 2006 tests execute in this project on the untouched tree;
#   - the plan MODIFIES the area, it does not create it;
#   - deterministic + cheap: one bounded, filtered `dotnet test` - no service boot, no network;
#   - strictly narrower than the terminal gate, which runs this project UNFILTERED (guardrails/02);
#   - two work tasks build on this area (01 authors its tests + stubs, 02 implements against them).
#
# The `!=` exclusion is the ONE place the plan-wide trait stands ALONE (#455). It exists so this
# preflight can say "everything except the tests this plan is about to write": the pre-DAG phase runs
# against the STARTING bytes, where none of the BacklogSlate tests exist yet, and the filter makes that
# intent explicit and robust. Bare, this trait is NOT a task-level selector - every task guardrail in
# this plan conjoins its OWN test class beside it (tasks 01/02 conjoin ~SampleVerifierTests, task 03
# conjoins ~SamplesCommandTests, tasks 04/05 conjoin ~SampleVerifierWiringTests).
#
# MEASURED, not recalled (#248) - run against this exact project and runner at authoring time
# (2026-08-29), with `dotnet test tests/Guardrails.Core.Tests --no-build --nologo`:
#   --filter "Category!=BacklogSlate"          -> Failed: 0, Passed: 2006, Skipped: 0, Duration 1 m 6 s
#     => `!=` INCLUDES tests that carry no Category trait at all, and INCLUDES tests carrying a
#        DIFFERENT Category. Every existing test in this project is one of those two - grep for
#        "BacklogSlate" over src/ and tests/ returns 0 hits on the untouched tree (positive control:
#        the same invocation for a literal known to be there, "PlanPreflightPhaseTests", returns 2, so
#        the search reached the trees rather than silently skipping them, #500). This filter therefore
#        does NOT vacuously select zero and red-halt before the DAG.
#   --list-tests --filter "Category!=BacklogSlate"      -> 2006 cases
#   --list-tests --filter "Category=TierResolution"     ->  146 cases
#   --list-tests --filter "Category!=TierResolution"    -> 1860 cases   (146 + 1860 = 2006, exactly)
#     => `!=` DOES exclude a test carrying the named trait; the two counts are exact complements over
#        the same 2006. So once the BacklogSlate tests exist, this preflight will genuinely skip them
#        rather than merely appearing to - and a zero match exits 0, which is why the executed-count
#        guard below is not optional.
#
# This check is POSITIVE / assert-present, so it is GREEN ON ARRIVAL by design (#479's named
# exception): a red here is a finding about the repo, not about this plan. It builds the project and
# then runs 2006 tests once, before the DAG - budget a couple of minutes, not seconds.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
$filter = 'Category!=BacklogSlate'
# NO --no-build here (unlike the authoring-time measurements above): the preflight runs against the
# STARTING repo, which may never have been built in this checkout, so it must build what it tests.
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
    Write-Output "the existing tests in tests/Guardrails.Core.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean the baseline is green - a --filter that matches
# nothing, or is malformed, also exits 0, and a "baseline green" verdict certified by a run that
# executed nothing is the catalogue's vacuous baseline. Key on the EXECUTED count (Passed+Failed;
# "Total:" would also count [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. tests/Guardrails.Core.Tests executed 2006 tests under this exact filter when this plan was authored, so zero executed means the filter or the project path is wrong, not that the area is empty."
    exit 1
}
exit 0
