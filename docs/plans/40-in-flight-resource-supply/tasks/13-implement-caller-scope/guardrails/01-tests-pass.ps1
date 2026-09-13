# catches: an implementation that does not actually satisfy the tests authored upstream —
#          and, via the zero-match guard, a filter that silently selects nothing (which exits 0
#          and would certify the task on an empty set, #455).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$out = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo --filter "FullyQualifiedName~SupplyCallerScopeTests" 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# ORDER MATTERS on the forward polarity (#455): exit-code check FIRST, so a test host that never
# ran is not misreported as a bad filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END so it reaches the ~60-line retry tail, #179) ==="
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

# Zero-match guard, keyed on the EXECUTED count (Passed: + Failed:), never Total: — which counts
# [Skip]ped tests, so a fully-skipped class would pass it.
$passed = 0
$failed = 0
if ($out -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($out -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "ZERO-MATCH: the filter 'FullyQualifiedName~SupplyCallerScopeTests' executed no tests. That exits 0 from dotnet test and would certify this task on an empty set."
    exit 1
}

exit 0
