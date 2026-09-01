# catches: a duration implementation whose behaviour deviates from the three behaviours THIS task owns -
#          most sharply, the three ways to be plausibly wrong here: reading the action's wall time back
#          off ProcessResult.Duration (deliberately ZEROED for a prompt action in
#          ActionRun.AsProcessResult, so every prompt attempt would report a confident 0 - the silent
#          direction); measuring ONE clock and assigning it to both ActionMs and GuardrailMs, which a
#          "both are non-null" test cannot see and which the authored not-equal assertion can; and
#          recording the segments on the SUCCESS settle only, which is section 2's survivorship finding
#          repeated with a new datum - an attempt that burned twenty minutes before going red is exactly
#          the evidence the corpus is missing.
#
#          It also catches the guardrail clock being started at ONE of TaskExecutor's two RunAsync call
#          sites: the prompt pins the measurement inside GuardrailRunner for that reason, and a
#          call-site clock reports nothing on the re-verify path.
#
#          The --filter names THIS task's OWN test class, never a plan-wide trait and never the whole
#          test FILE. AttemptEnvelopeTests.cs carries TWO classes: AttemptSegmentsTests (this task) and
#          AttemptTurnsTests (task 12, already landed). Filtering the file would make this task's green
#          depend on a sibling's tests as well as its own, and the two are separately owned (#455). This
#          plan introduces no trait at all, so this is shape 3: the single class term.
#          'AttemptSegmentsTests' was checked against all 195 existing Core test class names and every
#          other class this plan authors - it is a substring of none, and it is NOT a substring of
#          AttemptTurnsTests, so the filter selects this task's three tests and only those.
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

$filter = 'FullyQualifiedName~AttemptSegmentsTests'

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
    Write-Output "AttemptSegmentsTests is red. Fix GuardrailRunner.cs / TaskExecutor.cs / AttemptJournaler.cs - do NOT edit the test file, which is outside this task's writeScope and would fail the write-scope check. These assertions are LOWER BOUNDS, so a failure is an unmeasured phase, not a slow box. If ActionMs came back 0 or null on a prompt attempt, you read ProcessResult.Duration, which AsProcessResult zeroes for a prompt action - measure the wall time around the action call instead. If ActionMs and GuardrailMs came back equal, one clock is being assigned to both. If AFailedAttempt_StillRecordsItsSegments is the failure, the segments are journalled on the success settle only."
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
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The class AttemptSegmentsTests was not found, or the filter is malformed - this guardrail is certifying nothing. This is NOT a finding about the implementation."
    exit 1
}

Write-Output "AttemptSegmentsTests green: $executed tests executed, 0 failed."
exit 0
