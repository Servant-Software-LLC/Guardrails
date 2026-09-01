# catches: a Phase-1 change that is green in Core and broken at the CLI seam. This plan writes into
#          Guardrails.Cli twice - task 18 wires the run-environment probe into RunCommand's journal
#          creation, and tasks 22/24 change the telemetry report's rendering and add the census verb -
#          and NO Core test drives either. The shipped Integration suites that read exactly those
#          surfaces are TelemetryCommandTests (its Report_PrintsTheStratifiedTable pins the report's
#          "insufficient evidence" wording), TelemetryCommandWiringTests (verb registration through
#          CommandFactory) and RunEndTelemetryIngestTests (the run-end ingest path task 18 edits
#          alongside). A verb added to the wrong group, or a report column that shifts the table's
#          wording, fails only here.
#
# LOCAL by design (#165) - NO scope key in the sidecar, for the same reason as 02: a whole-suite run is
#          a terminal postcondition and would red-halt correct intermediate unions.
#
# NO -v q (#179/#462) - it would delete the Error Message / Expected / Actual block the re-emit needs.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this gate runs the whole Integration suite and cannot run without it."
    exit 1
}

$log = & dotnet test $project --nologo 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# EXIT CODE FIRST on a forward check (#455) - a host that never started exits non-zero with no summary.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Integration suite failure detail (re-emitted so it lands in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The Integration suite is red on the merged plan-branch HEAD. If TelemetryCommandTests or TelemetryCommandWiringTests failed, look at tasks 22 and 24 (both write TelemetryCommand.cs, serialized 22 -> 24); if RunEndTelemetryIngestTests failed, look at task 18's RunCommand.cs wiring."
    exit 1
}

$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

# Zero-match guard (#455), keyed on the EXECUTED count - never on 'Total:', which counts [Skip]ped tests.
if ($executed -lt 1) {
    Write-Output "INTEGRATION SUITE EXECUTED ZERO TESTS: exit was 0 but no test ran. This gate is certifying nothing - the test host did not start, or the project produced no tests."
    exit 1
}

Write-Output "Integration suite green on the merged HEAD: $executed tests executed, 0 failed."
exit 0
