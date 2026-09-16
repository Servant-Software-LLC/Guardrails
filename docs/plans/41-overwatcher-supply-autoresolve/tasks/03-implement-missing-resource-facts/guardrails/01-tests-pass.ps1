# catches: an implementation that does not satisfy the tests authored upstream — and, via the
#          zero-match guard, a filter that silently selects nothing (which exits 0 and would certify
#          the task on an empty set, #455).
#          Re-emits the assertion/exception block at the END so the WHY reaches the harness's
#          ~60-line retry-feedback tail, not just the [FAIL] names (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair owns TWO test classes: parenthesised alternation, BARE '|' (a backslash-escaped '\|' is
# rejected as "Incorrect format for TestCaseFilter", which matches zero tests and exits 0 — #455).
$filter = 'Category=OverwatchSupply&(FullyQualifiedName~MissingResourceSignalTests|FullyQualifiedName~MissingResourceFactsTests)'

# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find — which defeats #179 by the flag alone.
$out = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# EXIT CODE FIRST on the forward polarity (#455): a test host that never ran exits NON-zero with no
# summary at all, so checking the code first reports its real error instead of blaming the filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END for the ~60-line retry tail, #179) ==="
    # #608: a BLOCK capture, never a line allowlist. An allowlist drops the 'Failed <TestName>'
    # header (so the detail names no test) and 'System.NotImplementedException : ...' (which is the
    # dominant first-attempt failure of every implement task in this plan).
    $inBlock = $false
    $emitted = 0
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S') { $inBlock = $true }
        elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
        if ($inBlock -and $emitted -lt 40) { Write-Output $line; $emitted++ }
    }
    if ($emitted -eq 0) {
        Write-Output "(no per-test failure block in the runner output - the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)"
    }
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed — a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also
# count [Skip]ped tests, so a fully-skipped run would pass.
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The filter '$filter' matched no tests, is malformed, or every match is [Skip]ped. Check it against the two classes this task pair owns."
    exit 1
}

Write-Output "MissingResourceSignal/MissingResourceFacts: $ran test(s) executed, all passing."
exit 0
