# catches: buying the new event by breaking the old one. docs/plans/29-model-visibility-ux.md section 9
#          puts changing AttemptModelResolved, AttemptModelSummary and the grep-anchored console line
#          explicitly OUT OF SCOPE - and the cheapest wrong implementation of this task is not to ADD a
#          launch-time event but to RETARGET the existing post-action one, which looks identical in a
#          diff summary and destroys the confirm/correct pairing the design depends on.
#          The two suites named below are exactly the blast radius:
#            AttemptModelDisclosureTests  - drives a real attempt path and asserts WHICH raises happen,
#                                           in order, with which payload. A moved, duplicated or
#                                           deleted AttemptModelResolved raise fails here.
#            AttemptModelForwardingTests  - drives BOTH decorators through the IRunObserver interface
#                                           and asserts the model pair survives the trip, plus a
#                                           reflection sweep over every forwarding observer in the Cli
#                                           assembly. This task edits both decorators; a forward
#                                           deleted or mangled while adding the neighbour fails here.
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
#
# REGRESSION clause, green on arrival BY DESIGN - the declared #478 exception ("this existing thing
# still passes" is green before the task by definition). Neither file is in this task's writeScope, so
# there is no deletion risk to cover: the harness rejects an edit to them outright. A plain suite-pass
# is therefore sufficient here.
#
# scope: LOCAL (no sidecar). It asserts EXISTING suites still pass, which reads union-safe - but it is
# scoped to this task's own attempt deliberately, because attributing a model-disclosure or
# decorator-forwarding regression to the task that changed the observer contract is the entire value
# (#250).
#
# MEASURED 2026-08-29 on the untouched tree, prebuilt, with this exact filter: 8 tests executed,
# "Passed! - Failed: 0, Passed: 8", exit 0, in 5 s wall. Per class, measured separately:
# AttemptModelDisclosureTests 5, AttemptModelForwardingTests 3. Seconds, not minutes.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# dotnet.md 4.3 shape 2 (two classes, parenthesised alternation, BARE '|' - '\|' is VSTest's escape
# character and yields "Incorrect format for TestCaseFilter", ZERO tests, exit 0, a silent green).
# The plan-wide trait is ABSENT on purpose, and the two classes do NOT share one - MEASURED, not
# assumed: AttemptModelDisclosureTests carries a class-level [Trait("Category","AttemptModelDisclosure")]
# and AttemptModelForwardingTests carries NO Trait attribute at all. Conjoining Category=BacklogSlate
# would therefore match ZERO tests and the executed-count guard below would fire; so would conjoining
# either of the two classes' own categories, since neither covers both.
# FILTER DISCRIMINATION, measured against every *Tests class name under tests/: the only three classes
# whose names begin 'AttemptModel' are AttemptModelDisclosureTests, AttemptModelForwardingTests and
# AttemptModelRenderingTests, and neither substring below is contained in the third - so this selects
# exactly the two intended classes and nothing else.
$filter = '(FullyQualifiedName~AttemptModelDisclosureTests|FullyQualifiedName~AttemptModelForwardingTests)'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
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
    Write-Output "AttemptModelDisclosureTests / AttemptModelForwardingTests REGRESSED - adding the launch-time route event moved, duplicated or deleted the existing #349 attempt-model disclosure, or mangled one of the two decorator forwards. Both are OUT OF SCOPE for this task (design section 9): AttemptRouteResolved is ADDITIVE and sits BESIDE AttemptModelResolved, which keeps its four-argument signature, its wording and its post-action raise point. These suites passed before this task ran and are OUTSIDE its write scope: fix TaskExecutor.cs, IRunObserver.cs and the two decorators, not the tests."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this regression guard certified nothing. The --filter '$filter' matched no tests or is malformed. Both classes live in tests/Guardrails.Integration.Tests/ModelTiering/ (AttemptModelDisclosureTests.cs and AttemptModelForwardingTests.cs) and executed 8 tests between them when this guardrail was authored; do NOT 'fix' this by conjoining a Category term."
    exit 1
}
exit 0
