# catches: a fixture edit that broke the two tests outright - a wrong variable threaded into run 2, a
#          journal built from one plan and a scheduler from another, a load moved before the on-disk edit
#          instead of after it. All of those turn these tests RED immediately, and this is what says so.
#
# ============================ READ THIS BEFORE TRUSTING A GREEN HERE ============================
# THIS IS A DECLARED REGRESSION CLAUSE, NOT THE LOAD-BEARING CHECK. Both of these tests pass BEFORE this
# task and pass AFTER it. They are green on the untouched tree right now. A green here therefore proves
# only that nothing was BROKEN - it cannot distinguish a correct re-baseline from no edit at all.
#
# The reason is structural rather than a gap in this file: the behavioural difference these two fixtures
# exist to model appears only once STAGE 13's settle-time gate lands, and stage 13 `dependsOn` this task.
# At the moment this guardrail runs there is no runtime signal to assert on. Guardrail 02 - a source-shape
# check on the fixture's SHAPE - is the load-bearing one, and its own header states why it outranks a test
# here, which is the one place in this plan where that ordering inverts (#468).
#
# Do NOT "strengthen" this file by asserting a failure, and do NOT delete it as vacuous. Its job is the
# narrow one it claims: catch a re-baseline that broke the tests while making the shape check happy.
# ================================================================================================
#
# MEASURED BASELINE (#478): 2 tests selected, 2 passing, 0 failing on the untouched tree - GREEN ON
#          ARRIVAL BY DESIGN, and named as such rather than left to look like a clause that happens to be
#          pre-satisfied. This is the same declared-exemption shape the plan's positive preflights carry
#          (Step 7.0a's named exception), for the same reason: it asserts a precondition that is already
#          true and must stay true. The gate that makes it interesting is eleven tasks downstream.
#
#          Re-emits the assertion/exception lines at the END so a failure's WHY reaches the harness
#          retry-feedback tail, not just the [FAIL] name (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED (a German-culture box prints 'gesamt:' and no
# 'Total:'), which would invert the guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The two methods BY NAME, parenthesised with a BARE pipe. A backslash-escaped pipe is rejected by VSTest
# as an invalid condition and yields ZERO tests at exit 0 - a silent green (dotnet.md 4.3), which on a
# clause that is already green-on-arrival would be indistinguishable from success.
#
# Scoped to the two methods rather than the whole class DELIBERATELY: the other twelve facts in
# SchedulerWaveExecutionTests are outside this task's change and outside its business. The terminal gate
# runs the whole suite; a task-level clause that asserted all fourteen would fail this task for a sibling's
# reason, which is the #455 trap one level up.
$filter = '(FullyQualifiedName~SchedulerWaveExecutionTests.WaveDrift_CompletedWaveChanged_AutoPolicy_RewindsAndReRuns_WithWaveBoundaryDecision' +
          '|FullyQualifiedName~SchedulerWaveExecutionTests.PendingFutureWaveEdit_IsNotDrift_RunsNormally)'

# NO -v q on a TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block, leaving
# only the [FAIL] line for the re-emit below to find - defeating #179 by the flag alone (#462).
$out = & dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero with no
# summary at all, so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "one of the two wave-resume fixtures is RED after the re-baseline. These were GREEN before this task, so this is breakage you introduced, not a pre-existing condition. Most likely: run 2's journal and its scheduler were built from DIFFERENT plan objects, or the second b.Load() landed BEFORE the on-disk edit rather than after it - the reload must happen after File.WriteAllText, because modelling the resume is the entire point. Every assertion must survive untouched."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests. On a clause that is green-on-arrival this guard is doing more work than usual: without
# it, a mistyped method name is indistinguishable from success.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 2) {
    Write-Output "exit 0 but $ran test(s) executed where 2 were expected - this guardrail certified nothing. Either a method was RENAMED or DELETED (both forbidden by guardrail 02), or the filter is malformed. The two names are WaveDrift_CompletedWaveChanged_AutoPolicy_RewindsAndReRuns_WithWaveBoundaryDecision and PendingFutureWaveEdit_IsNotDrift_RunsNormally, in SchedulerWaveExecutionTests."
    exit 1
}
Write-Output "Wave-resume fixtures still green: $ran tests executed, none failed. (Regression clause only - see this file's header for why a green here is not evidence the re-baseline happened.)"
exit 0
