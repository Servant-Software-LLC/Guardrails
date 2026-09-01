# catches: an implementation whose behaviour deviates from the five behaviours THIS task pair owns -
#          most sharply, a bucket wired onto the SUCCESS path alone. Section 2 of the plan measured
#          that every one of 23 failed attempts in plan 27 carried no provenance, so each stratum kept
#          only its own successes and read 100% first-pass; a bucket that lands on successes alone
#          reproduces that survivorship defect one grain down, and FailedAttempt_JournalsTheBucketToo
#          is the row that catches it. It also catches the bucket being read off the task's NAME rather
#          than computed from writeScope and guardrails (two task ids, one bucket), a bucket that
#          changes between two attempts of the same task, and a null writeScope conflated with an empty
#          one.
#
#          The --filter names this pair's OWN test class, never a plan-wide trait - a trait-only filter
#          asserts the state of every test in the plan, so this task could not go green until a task
#          that DEPENDS on it had run (a deadlock validate and graph --check cannot see, #455). This
#          plan introduces no trait at all, so this is shape 3: the class term alone.
#          'TaskBucketJournalTests' was checked against all 194 Core test class names in the tree today
#          and against every other class this plan authors - it is a substring of none, so the filter is
#          discriminating.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179): default `dotnet test` prints them mid-run and ends with only [FAIL] <name>.
#
# NO -v q on the TEST command (#179/#462): it suppresses the entire Error Message / Expected / Actual /
#          Stack Trace block, leaving only "[FAIL] <name>" for the re-emit below to find.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:'), which would invert the
# zero-match guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail runs this pair's tests and cannot run without it."
    exit 1
}

$filter = 'FullyQualifiedName~TaskBucketJournalTests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# EXIT CODE FIRST on a forward (assert-pass) check (#455): a test host that never ran exits NON-zero
# with no summary at all, so checking the exit code first reports its real error instead of blaming the
# filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "TaskBucketJournalTests is red. Fix src/Guardrails.Core/Journal/RunJournal.cs and src/Guardrails.Core/Execution/AttemptJournaler.cs - do NOT edit the test file, which is outside this task's writeScope and would fail the write-scope check. If FailedAttempt_JournalsTheBucketToo is the failure, the bucket is wired onto the success path only: grep AttemptJournaler.cs for '_journal.RecordAttempt(' and pass the bucket at EVERY hit, including the invalid-fragment branch and FailedAttempt. If TheBucketIsStableAcrossARetryOfTheSameTask is the failure, a later call is passing null and CLEARING the recorded value: use the definitionHash precedent, 'Bucket = bucket ?? entry.Bucket' (RunJournal.cs:250-253)."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, so a fully-skipped class would clear a Total-keyed guard.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The class TaskBucketJournalTests was not found, or the filter is malformed - this guardrail is certifying nothing. This is NOT a finding about the implementation."
    exit 1
}

Write-Output "TaskBucketJournalTests green: $executed tests executed, 0 failed."
exit 0
