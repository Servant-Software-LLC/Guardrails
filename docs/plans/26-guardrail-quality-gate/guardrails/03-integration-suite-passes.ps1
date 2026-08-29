# catches: a green-per-task plan that regressed the CLI's real composition roots. This plan's two
#          riskiest edits both land in src/Guardrails.Cli, and tests/Guardrails.Integration.Tests is
#          the ONLY project in the solution that references Guardrails.Cli - so it is the only place
#          either regression is visible at all, and no task in this plan runs it unfiltered.
#
#          (1) Task 04 inserts a step into PlanPreflightPhase.EvaluateAsync, and its own prompt
#              requires that step to run on EVERY call, BEFORE both existing short-circuits - before
#              the "this plan declares no preflights/ folder" early return and before the resume SKIP.
#              That is deliberate and correct (a pair with reversed polarity must not stay invisible
#              for every plan that declares no Full Flight Checks), and it is also the widest blast
#              radius in the plan: the phase is reached from RunCommand, so the new code executes for
#              EVERY test in this project that drives a real run, including the great majority whose
#              temp plans have no preflights/ folder and previously returned at that first line. A step
#              that is merely SLOW, or that trips over a fixture plan whose tasks happen to carry a
#              samples/ directory, breaks tests nothing in this plan re-runs.
#          (2) Task 03 edits CommandFactory.BuildRootCommand, which 15 of this project's 125 source
#              files build through (MEASURED 2026-08-29, bin/ and obj/ excluded; positive control: the same invocation for
#              "namespace Guardrails.Integration.Tests" returns 124, so the search reached the tree,
#              #500). A registration line that throws, shadows a verb name, or changes the root
#              command's shape fails all of them at once and none of them individually here.
#          (3) And the shared-assembly hazard this project has already been bitten by: task 04 adds a
#              new test class to it, and xUnit runs collections in parallel, so a new class reshuffles
#              the scheduling of every other class. LiveDisplayCollection.cs in this very project
#              records the measured instance: the Stage 3 model-tiering merge (#201) "added 15
#              integration tests elsewhere, which was enough to shift scheduling and make an existing
#              race land. A green suite before that merge was luck, not proof" - and it went red on
#              ubuntu only, while windows and macos passed.
#
#          The arithmetic that makes this gate necessary rather than decorative: this project holds 900
#          test cases, 896 of which execute (4 are deliberately skipped). preflights/02 runs 32 of them
#          - the three classes that cover PlanPreflightPhase - as the green START; task 04's own
#          guardrail runs only its new SampleVerifierWiringTests class. The remaining 864 executed
#          cases are run by NOTHING in this plan until here.
#
#          Re-emits the assertion/exception lines at the END so a red terminal gate's WHY reaches the
#          operator, not just `[FAIL] <name>` (#179).
#
# scope: LOCAL (no sidecar `scope` key), deliberately, and this is the point most likely to be
# "corrected" by a later reader because the project is called Integration.Tests. The word in the
# sidecar has nothing to do with the word in the project name. A whole-suite run is a TERMINAL
# POSTCONDITION: scope:"integration" would re-run it at EVERY union point (SSOT 4.3), on partial
# merges where a downstream task has not run yet - so between task 03's merge and task 04's, a suite
# run would compile and execute against a CommandFactory that registers a verb whose preflight wiring
# does not exist yet. That is the #125 anti-pattern by name. It runs ONCE, at run end, on the merged
# HEAD. The plan's union-safe integration invariant is guardrails/04-union-artifacts-sound.ps1, which
# is where the scope:"integration" tag belongs and is the file that credits GR2028.
#
# NO --filter here, and that is not an oversight: the #455 rule governs TASK-LEVEL filters, whose job
# is to name the pair's own class. This is the terminal whole-suite gate for this project, and the
# whole hazard above lives in the 864 cases a filter would exclude.
#
# COST, stated plainly so nobody is surprised by it: THIS SUITE TAKES SEVEN TO TWELVE MINUTES. Its
# tests drive the real CLI over temp git repos, so they are minutes, not seconds, by construction.
# MEASURED TWICE on the untouched tree, 2026-08-29, both exit 0 with Failed: 0, Passed: 896,
# Skipped: 4, Total: 900:
#     7 m 11 s  -  --no-build, box otherwise idle
#    11 m 42 s  -  this exact script (which builds first), with one other dotnet test running
#                  concurrently on the same box
# The spread is contention, not flakiness, and it is worth knowing before someone reads a twelve-minute
# terminal gate as a hang. It runs once, at the end of the run, and it is the only thing standing
# between a green DAG and a broken CLI.
#
# That measurement is also why the guard below keys on Passed+Failed and not on Total: this suite skips
# 4 tests every run (opt-in real-provider smoke tests and a golden reproduction), so Total is 900 while the
# EXECUTED count is 896. A guard keyed on Total would happily certify a run in which every test was
# skipped.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --nologo 2>&1
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
    Write-Output "the Guardrails.Integration test suite is RED on the merged plan-branch HEAD (see failure details above). Read the failing class before the failing assertion: a failure OUTSIDE SampleVerifierWiringTests most likely means the new sample-verification step in PlanPreflightPhase.EvaluateAsync now runs for plans that used to return at its first early exit, or the CommandFactory registration changed the root command's shape for the 15 files that build through it."
    exit 1
}

# ZERO-MATCH GUARD (#455): a test host that started, executed nothing and exited 0 would otherwise
# certify the whole suite green over an empty run. Key on the EXECUTED count (Passed+Failed; "Total:"
# would also count [Skip]ped tests), never on a verbosity-dependent string (#248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this terminal gate certified nothing. tests/Guardrails.Integration.Tests executed 896 of its 900 cases (4 deliberately skipped) when this plan was authored, so zero executed means the project path is wrong or the test host never started, not that the suite is empty."
    exit 1
}
exit 0
