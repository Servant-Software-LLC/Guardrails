# catches: a Phase-1 datum that reaches its own task's test class and breaks a DIFFERENT one. Eleven of
#          this plan's tasks author a new Core test class and each task-level tests-pass guardrail is
#          --filter-scoped to ITS OWN class (#455), so no per-task guardrail can see a regression it
#          caused somewhere else. The concrete exposure: task 03 adds six members to JournalModel.cs and
#          task 20 bumps TelemetryRow.CurrentSchemaVersion, and the shipped Core suites read both -
#          TelemetryCorpusStoreTests asserts every written row carries CurrentSchemaVersion,
#          TelemetryIngestTests asserts the exact provenance fields the ETL copies, and four
#          definition-hash suites hash JournalModel's serialized shape. Only an UNFILTERED run of the
#          whole project sees those.
#
# LOCAL by design (#165) - NO scope key in the sidecar. A whole-suite run is a terminal postcondition:
#          at an intermediate union this plan's merged bytes contain deliberately-red TDD tests whose
#          implementation task has not integrated yet, so an integration-scoped whole-suite check would
#          red-halt a correct partial merge. It runs once, here, on the fully merged HEAD.
#
# NO -v q (#179/#462): on a TEST command -v q suppresses the entire Error Message / Expected / Actual /
#          Stack Trace block, leaving only "[FAIL] <name>" for the re-emit below to find - which defeats
#          the whole point of the re-emit by the flag alone.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:' and no 'Total:'), which would
# invert the executed-count guard below into an unconditional failure. Pin it before the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this gate runs the whole Core suite and cannot run without it."
    exit 1
}

$log = & dotnet test $project --nologo 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# EXIT CODE FIRST on a forward (assert-pass) check (#455): a test host that never started exits non-zero
# with no summary at all, so checking the code first reports its real error instead of blaming the count.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Core suite failure detail (re-emitted so it lands in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The Core suite is red on the merged plan-branch HEAD. If the failures are in TelemetryCorpusStoreTests, TelemetryIngestTests or any *DefinitionHash* suite, the cause is almost certainly task 03's journal members or task 20's schema-version bump reaching a shipped assertion that no task-level filter covers - rebaseline the shipped assertion, do not weaken the new one."
    exit 1
}

$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

# Zero-match guard (#455): exit 0 alone does not mean the suite ran. Keyed on the EXECUTED count
# (Passed + Failed), never on 'Total:' - which counts [Skip]ped tests, so a fully-skipped run would clear
# a Total-keyed guard and certify the suite green over nothing.
if ($executed -lt 1) {
    Write-Output "CORE SUITE EXECUTED ZERO TESTS: exit was 0 but no test ran. This gate is certifying nothing - the test host did not start, or the project produced no tests."
    exit 1
}

Write-Output "Core suite green on the merged HEAD: $executed tests executed, 0 failed."
exit 0
