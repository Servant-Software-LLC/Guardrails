# catches: an implementation whose behaviour deviates from the tests this pair owns — and, via the
#          zero-match guard, a filter that silently selects nothing (which exits 0 and would certify
#          the task on an empty set, #455).
#          The $filter is task 10's, copied VERBATIM: the two halves of a TDD pair must never drift.
#          It carries the plan trait AND the class term, which is what keeps the five shipped Drain_*
#          tests in scope here — "Drain's behaviour is unchanged after the refactor" (design 41 §5) is
#          proven by RUNNING them against the refactored Drain, and this is the run that does it.
#          Re-emits the assertion/exception lines at the END so they reach the ~60-line retry tail (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)

$filter = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedDrainTests'

# NO -v q on the TEST command (#462): it suppresses the whole Error Message / Expected / Actual /
# Stack Trace block, leaving only "[FAIL] <name>" for the re-emit below to find — which defeats #179
# by the flag alone.
$out = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo --filter $filter 2>&1 | Out-String
$testExit = $LASTEXITCODE                                  # captured BEFORE any other statement

Write-Output $out

# EXIT CODE FIRST on a forward check (#455): a test host that never ran exits NON-zero with no summary
# at all, so checking the code first reports its real error instead of confidently blaming the filter —
# which would point a retry at the one artifact it is NOT allowed to change (the test file is task 10's).
if ($testExit -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    # #608: a BLOCK capture, never a line allowlist. An allowlist drops the `Failed <TestName>` header,
    # the String:/Found: payload of a DoesNotContain failure, every stack FRAME, and
    # `System.NotImplementedException : ...` — the dominant first-attempt failure of this task.
    $inBlock = $false
    $emitted = 0
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $inBlock = $true }
        elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
        if ($inBlock -and $emitted -lt 40) { Write-Output $line; $emitted++ }   # bounded so it fits the tail
    }
    if ($emitted -eq 0) {
        Write-Output "(no per-test failure block in the runner output — the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)"
    }
    Write-Output "SuppliedDrainTests failing — CommitPaths (or the refactored Drain) does not behave as the authored tests require (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed — a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); "Total:" would also count
# [Skip]ped tests, so a fully-skipped run would certify this task on nothing.
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed — this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. It is task 10's filter, copied verbatim; do not 'fix' it here."
    exit 1
}

exit 0
