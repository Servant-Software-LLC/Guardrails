# catches: a brownfield plan building on a RED base - the EXISTING tests in tests/Guardrails.Core.Tests,
#          the area every task in this plan modifies, are already failing on the starting code. Asserting
#          them green BEFORE the DAG means a later task's tests-pass failure is attributable to THAT task
#          rather than to pre-existing breakage, and the new tests' TDD red is unambiguous (#181).
#          Re-emits the failure DETAIL at the END so a red baseline's WHY reaches the halt feedback, not
#          just `[FAIL] <name>` (#179).
#
# Scope: the AREA (this one test project), never the whole suite - a whole-solution test run hits the
# #165/#176 compile-coupling trap on a mid-TDD tree and dead-ends a run no work task can rescue.
#
# The `!=` exclusion is the ONE place the plan-wide trait stands ALONE (#455). It exists so this
# preflight can say "everything except the tests this plan is about to write": the pre-DAG phase runs
# against the STARTING bytes, where none of the PlanSourceProvenance tests exist yet, and the filter
# makes that intent explicit and robust. Bare, this trait is NOT a task-level selector - every task
# guardrail in this plan conjoins its OWN test class beside it.
#
# MEASURED, not recalled (#248) - the two halves of the `!=` semantics this preflight depends on, run
# against this exact project and runner at authoring time:
#   FullyQualifiedName~OverwatchClassifierTests                          -> Passed: 25
#   FullyQualifiedName~OverwatchClassifierTests&Category!=PlanSourceProvenance -> Passed: 25
#     => `!=` INCLUDES tests that carry no Category trait at all. Every existing test in this project
#        is one of those, so this filter does NOT vacuously select zero and red-halt before the DAG.
#   FullyQualifiedName~ActionTierProvenanceTests                         -> Passed: 13
#   FullyQualifiedName~ActionTierProvenanceTests&Category!=TierResolution -> "No test matches", exit 0
#     => `!=` DOES exclude a test carrying the named trait. So once the PlanSourceProvenance tests
#        exist, this preflight will genuinely skip them rather than merely appearing to.
#
# This check is POSITIVE / assert-present, so it is GREEN ON ARRIVAL by design (#479's named exception):
# a red here is a finding about the repo, not about this plan. It runs the WHOLE existing
# Guardrails.Core test suite (hundreds of tests) once, before the DAG - budget minutes, not seconds.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
$filter = 'Category!=PlanSourceProvenance'
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
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. tests/Guardrails.Core.Tests carries hundreds of existing tests, so zero executed means the filter or the project path is wrong, not that the area is empty."
    exit 1
}
exit 0
