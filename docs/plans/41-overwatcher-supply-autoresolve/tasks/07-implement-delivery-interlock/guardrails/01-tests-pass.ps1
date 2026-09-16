# catches: an implementation that does not satisfy the tests authored upstream - and, via the
#          zero-match guard, a filter that silently selects nothing (which exits 0 and would certify
#          the task on an empty set, #455).
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179) - default dotnet test prints them mid-run and ends with only [FAIL] <name>.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)

# The SAME $filter string as this pair's inverse half (tasks/06-.../guardrails/02-tests-fail-on-stubs.ps1),
# copied verbatim so the two halves of the TDD pair can never drift apart. Bare '|' in the alternation:
# '\|' is VSTest's escape character and yields a malformed filter, which exits 0 with ZERO tests.
$filter = 'Category=OverwatchSupply&(FullyQualifiedName~RunOutcomePolicyTests|FullyQualifiedName~WaveScopedInterlockTests)'

# No -v q on a TEST command: it suppresses the whole Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - defeating #179 by the flag alone (#462).
$out  = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# EXIT CODE FIRST on a forward check (#455): a test host that never ran exits NON-zero with no summary
# at all, so checking the code first reports its real error instead of blaming the filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END for the ~60-line retry tail, #179) ==="
    # #608: a BLOCK capture, never a line allowlist. An allowlist drops the `Failed <TestName>` header
    # and `System.NotImplementedException : ...` - the dominant first-attempt failure here, because the
    # stub predicate throws - leaving `Error Message:` followed by nothing.
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
    Write-Output ""
    Write-Output "The OverwatchSupply interlock rows are failing - HoldsDelivery is not implemented to spec (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); "Total:" would also count
# [Skip]ped tests, so a fully-skipped run would pass.
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the tests this task pair actually owns."
    exit 1
}
exit 0
