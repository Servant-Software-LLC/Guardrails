# catches: an implementation whose behaviour deviates from the five behaviours THIS task pair owns -
#          most sharply, two:
#            1. filling MaxParallelism from Environment.ProcessorCount. That is the plausible wrong
#               implementation of the record, because both numbers are "how parallel is this box", and
#               it is silently wrong: on a machine whose core count happens to equal the run's
#               concurrency it looks right, and in the corpus it makes every run on a big box appear to
#               have run at high concurrency. (A fabricated version default - an empty string, or the
#               harness version substituted for an absent skill version - is the same shape, where null
#               is the true and useful answer.)
#            2. a probe that returns a correct record which never reaches state/run.json. This task's
#               own prompt names the mechanism: RunJournal.LoadOrCreate is called at TWO independent
#               sites on a real run, and a stamp placed on the wrong side of the second one is
#               "silently lost" - the document is re-read from disk and continues from a state written
#               before the stamp. The four Core tests cannot see that: they stop at the probe. Nothing
#               observed the persistence hop at all until the round-trip test was added.
#
# TWO INVOCATIONS, ONE FAILURE LIST, and the reason is structural rather than stylistic: this pair owns
#          test classes in TWO DIFFERENT PROJECTS - RunEnvironmentTests in Guardrails.Core.Tests (the
#          probe) and RunEnvironmentJournalTests in Guardrails.Integration.Tests (the probe ->
#          RunJournal -> run.json round trip). BOTH are authored by task 17 and proved RED there; BOTH
#          are outside THIS task's writeScope, so the only way to move either is to fix the
#          implementation, the recorder or the call site.
#          A single `dotnet test` over the solution would run every other suite in both projects and
#          turn an unrelated red into this task's failure; a single project run would silently certify
#          half the pair - and it is the PERSISTENCE half that would go unchecked, which is where the
#          ordering hazard lives. So each project is run with its OWN class-scoped filter, its OWN
#          culture pin and its OWN executed-count zero-match guard, and the results ACCUMULATE (#179)
#          into one list dumped at the end - so ONE attempt learns about both halves instead of
#          discovering the second only after fixing the first.
#
#          Each --filter names one of THIS pair's OWN test classes, never a plan-wide trait - a
#          trait-only filter asserts the state of every test in the plan, so this task could not go green
#          until a task that DEPENDS on it had run (a deadlock validate and graph --check cannot see,
#          #455). This plan introduces no trait at all, so both are shape 3: the class term alone.
#          MEASURED 2026-09-01, not carried forward: 'RunEnvironmentTests' was checked against all 200
#          distinct class names declared in Guardrails.Core.Tests and 'RunEnvironmentJournalTests'
#          against all 152 in Guardrails.Integration.Tests - helper classes INCLUDED, the conservative
#          superset, since an FQN substring filter does not know which classes carry tests. Plus every
#          other class this plan authors. Each term is a substring of none of them, and none of them is
#          a substring of either, so both filters are discriminating.
#          In particular RunEnvironmentJournalTests does NOT contain RunEnvironmentTests - the 'Journal'
#          sits between 'RunEnvironment' and 'Tests' - so the Core filter would NOT have swept up the
#          round-trip class by accident had it stayed a single invocation. That near-miss is why this is
#          two runs and not one widened filter: the substring rescue people expect here does not exist.
#          NO ALTERNATION is needed or used: the two classes live in different PROJECTS, so each gets a
#          single-class filter of its own. If one ever does need an alternation, VSTest takes a BARE
#          pipe - an escaped one matches nothing, exits 0 and reports zero tests, which is a silent
#          green the executed-count guard below exists to catch.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179): default `dotnet test` prints them mid-run and ends with only [FAIL] <name>. With
#          two invocations the first run's raw log would otherwise bury the detail, so the re-emitted
#          lines are COLLECTED and printed once, after both runs, immediately before the failure list.
#
# NO -v q on either TEST command (#179/#462): it suppresses the entire Error Message / Expected /
#          Actual / Stack Trace block, leaving only "[FAIL] <name>" for the re-emit below to find.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:'), which would invert the
# zero-match guards into unconditional failures. Pinned once here and again inside the loop, before each
# invocation, so neither run can inherit a culture the other set (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$runs = @(
    [ordered]@{
        Project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
        Filter  = 'FullyQualifiedName~RunEnvironmentTests'
        Fix     = "Fix src/Guardrails.Core/Journal/RunEnvironmentProbe.cs - do NOT edit tests/Guardrails.Core.Tests/Journal/RunEnvironmentTests.cs, which is outside this task's writeScope and would fail the write-scope check. If TheProbeRecordsTheEffectiveConcurrency_NotTheConfiguredOne is the failure, you filled MaxParallelism from Environment.ProcessorCount: record the argument you were HANDED, since the CPU count describes the machine and the concurrency describes the run. If TheProbeRecordsTheVersionsItIsGiven_AndNullsItIsNotGiven is the failure, you substituted a default for a null version: a null skill version means no skill is installed, which is a true answer and not a gap to fill."
    }
    [ordered]@{
        Project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
        Filter  = 'FullyQualifiedName~RunEnvironmentJournalTests'
        Fix     = "The probe's record is not reaching state/run.json. Fix the recorder in src/Guardrails.Core/Journal/RunJournal.cs and the call site in src/Guardrails.Cli/Commands/RunCommand.cs - do NOT edit tests/Guardrails.Integration.Tests/Journal/RunEnvironmentJournalTests.cs, which task 17 authored and proved RED and which is outside this task's writeScope. The likeliest cause is ORDERING: RunJournal.LoadOrCreate is called at two independent sites on a real run, RunCommand's FIRST and SchedulerFactory.CreateExecutor's later, so a stamp written after the scheduler's load is read back over and silently lost. Stamp immediately after RunCommand's own LoadOrCreate. The second likeliest is a recorder that serializes this instance's in-memory document instead of RE-READING FROM DISK first - read RecordDelivery's comment block, which documents exactly that trap."
    }
)

# ACCUMULATE (#179): one distinguishable message per invocation, plus the detail lines, dumped once.
$failures = @()
$detail   = @()

foreach ($run in $runs) {
    $project = $run.Project
    $filter  = $run.Filter

    # PRECONDITION - the one legitimate early exit. A missing project is not a finding about the
    # implementation, and continuing would report the other half as if this half had been checked.
    if (-not (Test-Path $project)) {
        Write-Output "PRECONDITION: $project not found - this guardrail runs this pair's tests in BOTH projects and cannot run without it."
        exit 1
    }

    # Per-invocation culture pin (#455) - never inherited from the previous iteration.
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'

    Write-Output ""
    Write-Output "=== $project --filter $filter ==="
    $log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
    $code = $LASTEXITCODE

    Write-Output $log

    # EXIT CODE FIRST on a forward (assert-pass) check (#455): a test host that never ran exits NON-zero
    # with no summary at all, so checking the exit code first reports its real error instead of blaming
    # the filter.
    if ($code -ne 0) {
        $detail += "--- $filter ---"
        foreach ($line in ($log -split "\r?\n")) {
            if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
                $detail += $line
            }
        }
        $failures += "$filter is RED in $project. $($run.Fix)"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
    # or is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also
    # count [Skip]ped tests, and the Integration suite carries skipped tests today, so a fully-skipped
    # class would clear a Total-keyed guard while certifying nothing.
    $passed = 0
    $failed = 0
    if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
    if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
    $executed = $passed + $failed

    if ($executed -lt 1) {
        $failures += "FILTER MATCHED NOTHING: 0 tests executed for '$filter' in $project. The class was not found, or the filter is malformed - this half of the guardrail is certifying nothing. This is NOT a finding about the implementation."
        continue
    }

    Write-Output "$filter green: $executed tests executed, 0 failed."
}

if ($failures.Count -gt 0) {
    if ($detail.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
        $detail | ForEach-Object { Write-Output $_ }
    }
    Write-Output ""
    Write-Output "=== run-environment tests: $($failures.Count) of $($runs.Count) project runs did not go green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output ""
Write-Output "Run-environment tests green in both projects: RunEnvironmentTests (Core, the probe) and RunEnvironmentJournalTests (Integration, the round trip to state/run.json)."
exit 0
