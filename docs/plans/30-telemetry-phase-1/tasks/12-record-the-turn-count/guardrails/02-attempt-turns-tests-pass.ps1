# catches: a turn-count implementation whose behaviour deviates from the three behaviours THIS task owns
#          - most sharply, the two the plan's prose does not spell out: defaulting a SCRIPT attempt's
#          turn count to 0 (a claim that a model was invoked and took no turns, where null is the honest
#          answer - the same line TelemetryRow.CostUsd and AttemptRecord.Usage already draw), and
#          recording the count on the SUCCESS settle only. The second is section 2's finding repeated
#          with a new datum: every failure carrying no record is exactly how the corpus came to read
#          100% first-pass in every routed stratum.
#
#          It also catches the value being copied onto ActionRun and never reaching the journal - the
#          structurally-dead shape AttemptRecord.Usage shipped as once already (#475), with every
#          guardrail green.
#
#          The --filter names THIS task's OWN test class, never a plan-wide trait and never the whole
#          test FILE. AttemptEnvelopeTests.cs carries TWO classes: AttemptTurnsTests (this task) and
#          AttemptSegmentsTests (task 12a, which runs AFTER this one and is expected to be RED right
#          now). A filter that caught both would make this task un-greenable until its own successor had
#          run - a deadlock validate and graph --check cannot see (#455). This plan introduces no trait
#          at all, so this is shape 3: the single class term.
#          'AttemptTurnsTests' was checked against all 195 existing Core test class names and every other
#          class this plan authors - it is a substring of none, and it is NOT a substring of
#          AttemptSegmentsTests, so the filter selects this task's three tests and only those.
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
    Write-Output "PRECONDITION: $project not found - this guardrail runs this task's tests and cannot run without it."
    exit 1
}

$filter = 'FullyQualifiedName~AttemptTurnsTests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# EXIT CODE FIRST on a forward (assert-pass) check (#455): a test host that never ran exits NON-zero with
# no summary at all, so checking the exit code first reports its real error instead of blaming the
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
    Write-Output "AttemptTurnsTests is red. Fix ActionRunner.cs / TaskExecutor.cs / AttemptJournaler.cs - do NOT edit the test file, which is outside this task's writeScope and would fail the write-scope check. If AScriptAction_RecordsNoTurnCount is the failure, something defaults a script attempt's turn count to 0: a script runs no turns and null is the honest answer. If AFailedAttempt_StillRecordsItsTurnCount is the failure, the count is being journalled on the success settle only - the failure recorder needs it too. AttemptSegmentsTests failing is NOT this task's concern; it belongs to 12a and this filter does not select it."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, so a fully-skipped class would clear a Total-keyed guard.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The class AttemptTurnsTests was not found, or the filter is malformed - this guardrail is certifying nothing. This is NOT a finding about the implementation."
    exit 1
}

Write-Output "AttemptTurnsTests green: $executed tests executed, 0 failed."
exit 0
