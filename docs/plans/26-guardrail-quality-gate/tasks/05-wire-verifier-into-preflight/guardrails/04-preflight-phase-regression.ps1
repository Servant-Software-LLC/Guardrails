# catches: a sample-verification step that WORKS and breaks the phase it was inserted into. This task
#          edits src/Guardrails.Cli/PlanPreflightPhase.cs, whose pre-existing coverage is
#          PlanPreflightPhaseTests (12 executed cases: six [Theory] methods x two maxParallelism rows).
#          Guardrails 01/03 assert only the NEW behaviour; none of them selects a single pre-existing
#          case. So a step that narrates itself on the empty path, writes a planPreflights marker for a
#          plan that declared none, launches a process where today there is none, or slips past the B1
#          resume SKIP would go GREEN on 01/02/03 and surface only at the terminal Integration gate -
#          which runs AFTER every task has merged, at a gate no task can fix.
#
# THE TRADEOFF, NAMED HONESTLY (#193). These 12 cases live in tests/Guardrails.Integration.Tests/
# PlanPreflightPhaseTests.cs, which is OUTSIDE this task's writeScope (src/Guardrails.Cli/
# PlanPreflightPhase.cs only). A guardrail that requires a task to make green a test the task may not
# edit is a dead-end whenever the correct implementation genuinely has to change that test. It does not
# here, and the reason is structural rather than hopeful: the change is purely ADDITIVE, the prompt
# forbids changing EvaluateAsync's signature (its callers are all out of scope too), and it requires the
# no-committed-pairs path to be behaviourally byte-identical to today. Every one of these 12 fixtures
# builds a plan with no samples/ folder, so all 12 exercise exactly that path. If a future revision of
# this task ever DID have to re-baseline these tests, this guardrail becomes a #193 dead-end and the
# remedy is to widen the writeScope to own them - not to weaken this check.
#
# GREEN ON ARRIVAL, by construction and by design (#479's named exception for a regression check): these
# 12 cases pass on the untouched tree - MEASURED 2026-08-29 as part of the 32-case, three-class run in
# <plan>/preflights/02-baseline-preflight-phase-tests-green.ps1 (Failed: 0, Passed: 32). A red here is a
# regression THIS task introduced, which is the only thing it is asked to detect.
#
# scope: LOCAL (no sidecar). It asserts a property of this task's own edit, evaluated in this task's own
# segment, and says nothing about a union - so it must not be tagged scope:"integration" (#125/#250).
# Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the $ran guard below reads is LOCALIZED (#455)
$filter = 'FullyQualifiedName~PlanPreflightPhaseTests'
# ~PlanPreflightPhaseTests is DISCRIMINATING (#455/#193), and it was measured rather than assumed:
# <plan>/preflights/02 ran the three-term alternation
#   ~PlanPreflightPhaseTests|~PlanGuardrailPhaseTests|~GateFailurePersistenceTests
# with --list-tests and read the full listing: 12 of the 32 cases were PlanPreflightPhaseTests and ZERO
# cases fell outside the three named classes. `PlanGuardrailPhaseTests` does not contain this substring.
# The class this plan authors, SampleVerifierWiringTests, shares no substring with it either, so this
# filter can never sweep in the tests guardrail 03 owns.
#
# NO Category conjunct: PlanPreflightPhaseTests carries [Trait("Category", "Preflights")] at the class
# level, but adding it buys nothing here (the class-name term already selects exactly this class) while
# adding a filter-grammar failure mode.
#
# NO --no-build, deliberately, and it was MEASURED that this matters (2026-08-29). With --no-build a
# test guardrail reads whatever is in bin/, not what is in the SOURCE tree: a sibling census in this
# plan was observed exiting 0 over five STALE tests still compiled into the assembly after their source
# file had been deleted. 02-build-passes normally refreshes it first, so the window is narrow - but a
# single-guardrail `revalidate` re-runs this out of order, and a regression check that can certify a
# stale assembly is worthless. The incremental build costs ~15s against a 71s test run.
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST, guard second (#455, forward polarity): a test host that never ran exits NON-zero with
# no summary, so checking the exit code first reports its real error instead of blaming the filter and
# sending the retry agent to rename a correctly-named class it cannot edit anyway.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the PRE-EXISTING PlanPreflightPhaseTests coverage is failing - the sample-verification step you added changed the behaviour of the phase on a path it was not supposed to touch. Every one of these 12 fixtures builds a plan with NO samples/ folder, so on that path your step must be a silent no-op: no extra console line, no journal section a plan without preflights/ never had, no process launch, and no interference with the B1 resume SKIP. These tests are OUTSIDE your write scope - do not try to edit them; fix the phase (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean the regression check ran - a --filter that matches
# nothing, or is malformed, also exits 0, and a "no regression" verdict certified by a run that executed
# nothing is exactly the false green this plan exists to end. Key on the EXECUTED count (Passed+Failed;
# "Total:" would also count [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this regression check certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. PlanPreflightPhaseTests executed 12 cases under this exact filter when this plan was authored, so zero executed means a mistyped class name, a wrong project path, or a build output that was never produced - not that the class is empty."
    exit 1
}
exit 0
