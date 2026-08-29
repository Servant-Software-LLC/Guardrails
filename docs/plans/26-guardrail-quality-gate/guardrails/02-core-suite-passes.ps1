# catches: a green-per-task plan that broke something it never looked at. Be exact about what the
#          hazard is for THIS plan, because the obvious version of it does not apply: NO TASK HERE
#          EDITS AN EXISTING Guardrails.Core SOURCE FILE. The Core-side surface is two NEW files -
#          src/Guardrails.Core/Samples/SampleVerifier.cs and
#          tests/Guardrails.Core.Tests/Samples/SampleVerifierTests.cs - so "we changed shared code and
#          regressed a neighbour" is not the risk here and this header will not pretend it is.
#
#          The risk that IS real is the one this repo has already been bitten by: ADDING TESTS TO A
#          SHARED TEST ASSEMBLY CAN BREAK ITS NEIGHBOURS, and no task-level --filter can see it. xUnit
#          puts each test class in its own collection and runs collections IN PARALLEL, so a new class
#          changes the scheduling of every other class in the assembly. The repo's own
#          LiveDisplayCollection.cs records the measured instance verbatim: the Stage 3 model-tiering
#          merge (#201) "added 15 integration tests elsewhere, which was enough to shift scheduling and
#          make an existing race land. A green suite before that merge was luck, not proof" - and it
#          went red on ubuntu only. The tests task 01 authors are exactly the shape that does this:
#          SampleVerifier RUNS guardrail scripts, so its tests SPAWN PROCESSES and build temp
#          directories, and two existing files in this very project (ReVerifierSeamTests.cs,
#          WorktreeRootAndPathPreflightTests.cs) already mutate process-wide state
#          (Environment.CurrentDirectory / environment variables). A new test class that leaks a temp
#          dir, changes the current directory, or simply slows the assembly down is enough.
#
#          It is also the second half of the #181 baseline: green START before the DAG
#          (preflights/01-baseline-core-tests-green.ps1, 2006 tests under Category!=BacklogSlate),
#          green END on the merged HEAD (here, the same project with NO filter, so this plan's own new
#          tests are included too). Re-emits the assertion/exception lines at the END so a red terminal
#          gate's WHY reaches the operator, not just `[FAIL] <name>` (#179).
#
# scope: LOCAL (no sidecar `scope` key), deliberately. A whole-suite run is a TERMINAL POSTCONDITION.
# Tagging it scope:"integration" would re-run it at every union point, on partial merges where a
# downstream task has not run yet - so task 01's deliberately-RED tests, sitting on the plan branch
# from the moment task 01 merges until task 02 does, would red-halt a correct run. That is the #125
# anti-pattern by name, and a serial chain is not exempt: the harness re-verifies the integration set
# at every union regardless of topology.
#
# NO --filter here, and that is not an oversight: the #455 rule governs TASK-LEVEL filters, whose job is
# to name the pair's own class. This is the terminal whole-suite gate, the one place "all tests pass"
# belongs, so there is nothing to scope it to - and scoping it would defeat the neighbour-breakage
# hazard above, which lives entirely in the classes no filter selects.
#
# MEASURED 2026-08-29 on the untouched tree, with --no-build: this project runs 2006 cases in ~1 m 6 s,
# all green. Budget a couple of minutes here, since this gate builds first.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Guardrails.Core test suite is RED on the merged plan-branch HEAD (see failure details above). If the failing test is NOT SampleVerifierTests, suspect the new class rather than the old one: adding a process-spawning test class to this assembly shifts xUnit's parallel scheduling and can land a latent race in a neighbour nothing in this plan touched (the LiveDisplayCollection.cs precedent)."
    exit 1
}

# ZERO-MATCH GUARD (#455): a test host that started, executed nothing and exited 0 would otherwise
# certify the whole suite green over an empty run. Key on the EXECUTED count (Passed+Failed; "Total:"
# would also count [Skip]ped tests), never on a verbosity-dependent string (#248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this terminal gate certified nothing. tests/Guardrails.Core.Tests executed 2006 tests when this plan was authored, so zero executed means the project path is wrong or the test host never started, not that the suite is empty."
    exit 1
}
exit 0
