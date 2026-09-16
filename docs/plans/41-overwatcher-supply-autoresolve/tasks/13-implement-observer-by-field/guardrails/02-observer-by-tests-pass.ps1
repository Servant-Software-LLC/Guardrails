# catches: an implementation whose behaviour deviates from the tests this pair owns — and, via the
#          per-suite zero-match guard, a filter that silently selects nothing (which exits 0 and would
#          certify the task on an empty set, #455).
#          BOTH $filter strings are task 12's, copied VERBATIM: the two halves of a TDD pair must never
#          drift. TWO PROJECTS, deliberately — this pair's evidence is split across Guardrails.Core.Tests
#          (the event's shape, the core decorators, the member-replacement assertion) and
#          Guardrails.Integration.Tests (the CLI renderings, the whole-chain artifact proof). A single
#          project path would report success over half the evidence.
#          Re-emits the assertion/exception lines at the END so they reach the ~60-line retry tail (#179).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guards below read is LOCALIZED (#455)

$suites = @(
    @{
        Name    = 'Core'
        Project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
        Filter  = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedObserverEventTests'
    },
    @{
        Name    = 'Integration'
        Project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
        # Parenthesised alternation with a BARE '|': '\|' is VSTest's escape character and is rejected
        # as an invalid condition — zero tests, exit 0, a silent green.
        Filter  = 'Category=OverwatchSupply&(FullyQualifiedName~SuppliedObserverCliForwardingTests|FullyQualifiedName~ObserverForwardingSweepTests)'
    }
)

foreach ($suite in $suites) {
    # NO -v q on the TEST command (#462): it suppresses the whole Error Message / Expected / Actual /
    # Stack Trace block, leaving only "[FAIL] <name>" for the re-emit below to find.
    $out = & dotnet test $suite.Project -c Debug --nologo --filter $suite.Filter 2>&1 | Out-String
    $testExit = $LASTEXITCODE                              # captured BEFORE any other statement

    Write-Output $out

    # EXIT CODE FIRST on a forward check (#455): a test host that never ran exits NON-zero with no
    # summary at all, so checking the code first reports its real error instead of confidently blaming
    # the filter — which would point a retry at the one artifact it is NOT allowed to change.
    if ($testExit -ne 0) {
        Write-Output ""
        Write-Output "=== $($suite.Name) failure details (re-emitted so they land in the harness feedback tail) ==="
        # #608: a BLOCK capture, never a line allowlist — an allowlist drops the `Failed <TestName>`
        # header, the String:/Found: payload of a Contains/DoesNotContain failure (for the rendering
        # tests, Found: IS the finding), and every stack FRAME.
        $inBlock = $false
        $emitted = 0
        foreach ($line in ($out -split "`r?`n")) {
            if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $inBlock = $true }
            elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
            if ($inBlock -and $emitted -lt 40) { Write-Output $line; $emitted++ }   # bounded to fit the tail
        }
        if ($emitted -eq 0) {
            Write-Output "(no per-test failure block in the runner output — the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)"
        }
        Write-Output "The $($suite.Name) observer tests are failing — SuppliedResourcesCommitted does not carry or render `by` as the authored tests require (see failure details above)."
        exit 1
    }

    # ZERO-MATCH GUARD (#455), per suite: exit 0 alone does NOT mean tests passed. Key on the EXECUTED
    # count (Passed + Failed); "Total:" would also count [Skip]ped tests.
    $ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        Write-Output "exit 0 but ZERO tests executed in the $($suite.Name) suite — this guardrail certified nothing there. The --filter '$($suite.Filter)' matched no tests, is malformed, or every matched test is [Skip]ped. It is task 12's filter, copied verbatim; do not 'fix' it here."
        exit 1
    }
}

exit 0
