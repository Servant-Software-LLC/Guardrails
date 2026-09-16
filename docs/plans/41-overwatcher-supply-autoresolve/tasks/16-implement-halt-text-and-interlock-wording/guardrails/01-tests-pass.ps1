# catches: an implementation that does not satisfy the tests task 15 authored - and, via the zero-match
#          guard, a --filter that silently selects nothing (which exits 0 and would certify this task on
#          an empty set, #455).
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (the last ~60 lines of stdout); default dotnet test prints them mid-run and ends with
#          only "[FAIL] <name>" plus the count, so the tail would otherwise show WHAT failed, not WHY
#          (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)

# SAME string as task 15's inverse half - copied verbatim, so the two halves of the pair cannot drift.
$filter = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedHaltTextTests'

# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone (#462).
$out  = & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
    --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary, so
# checking the exit code first reports its real error instead of blaming the filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END for the ~60-line retry tail, #179) ==="
    # #608: a BLOCK capture, never a line allowlist. An allowlist drops the `Failed <TestName>` header and
    # the `String:`/`Found:` payload of a DoesNotContain failure - and three of the five pinned behaviours
    # here are negative assertions, where `Found:` IS the finding.
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
    Write-Output "SuppliedHaltTextTests failing - the halt text is not on the shared MissingResourceSignal predicate, or the interlock wording still names only the two best-guess/unreviewed tokens (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the tests this task pair actually owns."
    exit 1
}

Write-Output "SuppliedHaltTextTests green: $ran test(s) executed, all passing."
exit 0
