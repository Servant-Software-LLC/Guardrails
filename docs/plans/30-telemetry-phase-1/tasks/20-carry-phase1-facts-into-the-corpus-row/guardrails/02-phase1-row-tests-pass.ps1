# catches: a mapping whose behaviour deviates from the eight behaviours THIS task pair owns - most
#          sharply the two a hurried reading gets wrong. (a) Editing the per-attempt row and forgetting
#          the Attempt==0 task-grain sentinel: that is the row a reader strata on, and the one whose
#          bucket section 3.2 exists to fill. It is the single most likely wrong implementation here,
#          because it makes six of the eight tests pass and leaves two red in a way that reads like two
#          odd test failures rather than a missed grain. (b) Synthesizing a value for an unreported fact
#          (`?? 0`, `?? false`, `?? string.Empty`) - section 15.2's null-versus-zero rule, which says a
#          runner that reported nothing must not make the corpus assert the attempt took no time.
#
#          It also catches the half-mapped run environment: seven members that must reach EVERY row of
#          BOTH grains, where copying six and dropping one is invisible to a build and to any eyeball
#          reading the diff.
#
# TWO OF THE EIGHT ROWS ARE GREEN ON ARRIVAL, and this guardrail is what keeps them green.
#          TheSchemaVersionSaysTheRowShapeChanged passes because 04a bumped the constant and the ETL
#          already stamps it symbolically; AnUnreportedPhase1Fact_StaysNull_NotZero passes because the
#          columns exist and nothing populates them. The second is the sharp one: the ONLY way to redden
#          it is to add exactly the coalesce (b) forbids, so a suite-level green here is a positive proof
#          that no unreported fact was turned into a value.
#
#          The --filter names this pair's OWN test class, never a plan-wide trait - a trait-only filter
#          asserts the state of every test in the plan, so this task could not go green until a task that
#          DEPENDS on it had run (a deadlock validate and graph --check cannot see, #455). This plan
#          introduces no trait at all, so this is shape 3: the class term alone.
#          'Phase1TelemetryRowTests' was checked against all 328 existing test class names in the repo
#          and every other class this plan authors - it is a substring of none of them, and contains none
#          of them, so the filter is discriminating in both directions.
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

$filter = 'FullyQualifiedName~Phase1TelemetryRowTests'

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
    Write-Output "Phase1TelemetryRowTests is red. Fix src/Guardrails.Core/Telemetry/TelemetryIngest.cs - do NOT edit the test file or TelemetryRow.cs, both of which are outside this task's writeScope and would fail the write-scope check. Check the two usual causes in order: (a) only ONE of the two 'new TelemetryRow { ... }' sites in TelemetryIngest.cs was edited - the task-grain sentinel at line 61 needs Bucket and the seven run-environment members just as much as the attempt row at line 79 does, and this is what TheTaskGrainRowCarriesTheBucketToo and EveryRowCarriesTheRunEnvironment are for; (b) an unreported fact was coalesced to a value ('?? 0', '?? false', '?? string.Empty') instead of left null - if AnUnreportedPhase1Fact_StaysNull_NotZero is the failure, that is certainly it, because that test was GREEN before you started and a coalesce is the only way to redden it."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, and the real suite has skipped tests today, so a fully-skipped class would clear a
# Total-keyed guard.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The class Phase1TelemetryRowTests was not found, or the filter is malformed - this guardrail is certifying nothing. This is NOT a finding about the implementation."
    exit 1
}

Write-Output "Phase1TelemetryRowTests green: $executed tests executed, 0 failed."
exit 0
