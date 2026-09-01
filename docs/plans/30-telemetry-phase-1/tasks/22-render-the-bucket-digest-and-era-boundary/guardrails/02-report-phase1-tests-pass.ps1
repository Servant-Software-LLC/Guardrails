# catches: an implementation whose behaviour deviates from the five behaviours THIS task pair owns -
#          most sharply the three a hurried reading gets wrong. (a) Sourcing the bucket from the ATTEMPT
#          row when the task-grain row has one: the fallback chain at TelemetryCommand.cs:431-433 is
#          documented as prefer-task-row, fall-back-to-attempt, never a second opinion - inverting it
#          renders one attempt's opinion as the task's fact. (b) Folding the digest into the fingerprint
#          for EVERY row rather than only where a digest exists, which moves every legacy row's stratum
#          and is caught by AnUnbucketedLegacyRow_StillRendersUnbucketed and by the shipped
#          TelemetryCommandTests. (c) Filtering the era boundary the wrong side of the comparison, which
#          excludes the post-fix rows and keeps the survivorship ones - a defect that looks like a
#          working filter until someone reads the numbers.
#
#          It also catches the honesty regression section 5 forbids: a legend sentence deleted rather
#          than re-worded. AnUnbucketedLegacyRow_StillRendersUnbucketed is GREEN on arrival and must stay
#          green, so an implementation that stops rendering the (unbucketed) sentinel reddens a test that
#          was already passing - which is the loudest signal this plan has for "you removed a caveat".
#
#          The --filter names this pair's OWN test class, never a plan-wide trait - a trait-only filter
#          asserts the state of every test in the plan, so this task could not go green until a task that
#          DEPENDS on it had run (a deadlock validate and graph --check cannot see, #455). This plan
#          introduces no trait at all, so this is shape 3: the class term alone.
#          'TelemetryReportPhase1Tests' was checked against all 328 existing test class names in the repo
#          and every other class this plan authors: it is a substring of none of them and contains none
#          of them - in particular it neither contains nor is contained by the shipped
#          TelemetryReportTests - so the filter is discriminating in both directions.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179): default `dotnet test` prints them mid-run and ends with only [FAIL] <name>.
#
# NO -v q on the TEST command (#179/#462): it suppresses the entire Error Message / Expected / Actual /
#          Stack Trace block, leaving only "[FAIL] <name>" for the re-emit below to find - and on a
#          rendered-output assertion that block IS the finding, because it prints the table the report
#          actually produced beside the one the test expected.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:'), which would invert the
# zero-match guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail runs this pair's tests and cannot run without it."
    exit 1
}

$filter = 'FullyQualifiedName~TelemetryReportPhase1Tests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# EXIT CODE FIRST on a forward (assert-pass) check (#455): a test host that never ran exits NON-zero with
# no summary at all, so checking the exit code first reports its real error instead of blaming the filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "TelemetryReportPhase1Tests is red. Fix src/Guardrails.Cli/Commands/TelemetryCommand.cs - do NOT edit the test file, which is outside this task's writeScope and would fail the write-scope check. Check in order: (a) the bucket is sourced task-row-first then attempt-row then UnbucketedBucket, mirroring the Tier chain one line above it; (b) Fingerprint appends '@<digest>' ONLY when the row carries a ModelDigest, leaving a digestless row byte-identical to today; (c) the boundary comparison excludes rows STARTED BEFORE 2026-08-31T00:00:00Z and keeps everything at or after it; (d) the legend prints the literal '2026-08-31' and the word BOUNDARY. If AnUnbucketedLegacyRow_StillRendersUnbucketed is the failure, you removed the (unbucketed) sentinel - section 5 of the plan puts the report's honesty rules out of scope, so re-word that legend sentence, never delete it."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, and the real Integration suite has 4 skipped tests today, so a fully-skipped class
# would clear a Total-keyed guard.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The class TelemetryReportPhase1Tests was not found, or the filter is malformed - this guardrail is certifying nothing. This is NOT a finding about the implementation."
    exit 1
}

Write-Output "TelemetryReportPhase1Tests green: $executed tests executed, 0 failed."
exit 0
