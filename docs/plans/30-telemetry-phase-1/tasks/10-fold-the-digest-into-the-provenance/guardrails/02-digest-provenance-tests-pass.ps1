# catches: a fold whose behaviour deviates from the four behaviours THIS task pair owns - most sharply,
#          the three that are easy to get half-right: copying the digest onto ActionRun and never folding
#          it onto the provenance (the datum stops one hop short and nothing reports the loss); adding a
#          SECOND `with` expression instead of extending the existing one, so one fold's result is
#          discarded and the observed-model half or the digest half silently disappears; and filling an
#          ABSENT digest with "" or with the model tag, which makes two quantizations of one model pool
#          as one sample - the exact failure section 3.3 exists to prevent.
#
#          It also catches the placement mistake: TheDigestRidesTheProvenance_SoItReachesBothSettlePaths
#          asserts by reflection that ModelDigest is on AttemptProvenance and NOT on AttemptRecord, so a
#          "helpful" mirror onto the record goes red here rather than silently vanishing in worktree mode
#          months later.
#
#          The --filter names this pair's OWN test class, never a plan-wide trait - a trait-only filter
#          asserts the state of every test in the plan, so this task could not go green until a task that
#          DEPENDS on it had run (a deadlock validate and graph --check cannot see, #455). This plan
#          introduces no trait at all, so this is shape 3: the class term alone.
#          'ModelDigestProvenanceTests' was checked against all 195 existing Core test class names and
#          every other class this plan authors - it is a substring of none, so the filter is
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

$filter = 'FullyQualifiedName~ModelDigestProvenanceTests'

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
    Write-Output "ModelDigestProvenanceTests is red. Fix src/Guardrails.Core/Execution/ActionRunner.cs and src/Guardrails.Core/Execution/TaskExecutor.cs - do NOT edit the test file, which is outside this task's writeScope and would fail the write-scope check. If TheDigestSurvivesBesideTheObservedModelFold is the failure, you added a SECOND `with` expression instead of extending the existing one (grep 'ObservedModel is { } observedModel'): a `with` whose result is discarded changes nothing. If TheDigestRidesTheProvenance_SoItReachesBothSettlePaths is the failure, you put ModelDigest on AttemptRecord - it belongs on AttemptProvenance, which is the member that rides PendingAttempt and therefore reaches the worktree settle."
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
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The class ModelDigestProvenanceTests was not found, or the filter is malformed - this guardrail is certifying nothing. This is NOT a finding about the implementation."
    exit 1
}

Write-Output "ModelDigestProvenanceTests green: $executed tests executed, 0 failed."
exit 0
