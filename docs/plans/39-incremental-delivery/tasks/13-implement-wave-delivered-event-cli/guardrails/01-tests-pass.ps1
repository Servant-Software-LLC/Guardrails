# catches: an implementation that does not satisfy the tests authored upstream — and, via the
#          zero-match guard, a filter that silently selects nothing (which exits 0 and would certify
#          the task on an empty set, #455).
#
#          It runs against Guardrails.Integration.Tests, where task 11 writes
#          WaveDeliveredCliForwardingTests and the ONLY test project referencing Guardrails.Cli
#          (review 2026-09-13, finding B1: pointed at Guardrails.Core.Tests, this filter could never
#          match, so every attempt ended ZERO-MATCH whatever the agent did).
#
#          It ALSO runs LogSiteHaltBannerTests (review of 1a809bce, adversarial N8). That class pins the
#          exported log site byte for byte, is in no task's writeScope, and this task's prompt names it:
#          OnTheFlyLogSiteObserver is in this task's scope, so a delivery rendering that changed the page
#          of a run that delivered nothing used to surface only at the plan-root terminal gate, where no
#          task may edit tests. Both classes live in Guardrails.Integration.Tests, so one filter selects
#          both. BOUNDARY: the zero-match guard below counts the run as a whole, so it cannot see ONE half
#          of the filter matching nothing. Neither class is in this task's writeScope, so this task cannot
#          rename either one.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'FullyQualifiedName~WaveDeliveredCliForwardingTests|FullyQualifiedName~LogSiteHaltBannerTests'
$out = & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# Exit-code check FIRST on the forward polarity: a test host that never ran must not be misreported
# as a bad filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END for the ~60-line retry tail, #179) ==="
    # #608: a BLOCK capture, never a line allowlist. MEASURED against real xunit.v3 + VSTest
    # output: an allowlist drops the `Failed <TestName>` header (so the detail names no test)
    # and `System.NotImplementedException : ...` (so the dominant first-attempt failure of every
    # implement task in this plan re-emits as `Error Message:` followed by nothing).
    $inBlock = $false
    $emitted = 0
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S') { $inBlock = $true }
        elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
        if ($inBlock) { Write-Output $line; $emitted++ }
    }
    if ($emitted -eq 0) {
        Write-Output "(no per-test failure block in the runner output - the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)"
    }
    exit 1
}

$passed = 0
$failed = 0
if ($out -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($out -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "ZERO-MATCH: the filter '$filter' executed no tests — that exits 0 and would certify this task on an empty set."
    exit 1
}

exit 0
