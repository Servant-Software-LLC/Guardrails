# catches: an implementation that does not satisfy the tests authored upstream — and, via the
#          zero-match guard on EACH run, a filter that silently selects nothing (which exits 0 and would
#          certify the task on an empty set, #455).
#
#          TWO runs, both required (review 2026-09-13, C-B4/A-B3). The first is this task's own class.
#          The second re-runs task 28's WaveDeliveryWiringTests, which drive the Scheduler's barrier
#          delivery over a recording provider. This task edits that same barrier in Scheduler.cs, and
#          without the second run a regression there surfaced only at the plan-root terminal gate, where
#          no task may edit tests. The class lives in Guardrails.Core.Tests, the OTHER test project, so a
#          filter clause cannot select it: it needs its own dotnet test.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$runs = @(
    @{ Project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'; Filter = 'FullyQualifiedName~BranchMovedHaltTests' },
    @{ Project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'; Filter = 'FullyQualifiedName~WaveDeliveryWiringTests' }
)

$problems = @()
$details = @()
foreach ($run in $runs) {
    $out = & dotnet test $run.Project -c Debug --nologo --filter $run.Filter 2>&1 | Out-String
    $code = $LASTEXITCODE

    Write-Output "=== dotnet test $($run.Project) --filter $($run.Filter) (exit $code) ==="
    Write-Output $out

    # Exit-code check FIRST on the forward polarity: a test host that never ran must not be misreported
    # as a bad filter.
    if ($code -ne 0) {
        $problems += "FAILED: '$($run.Filter)' in $($run.Project) exited $code."
        # #608: a BLOCK capture, never a line allowlist. MEASURED against real xunit.v3 + VSTest
        # output: an allowlist drops the `Failed <TestName>` header (so the detail names no test)
        # and `System.NotImplementedException : ...` (so the dominant first-attempt failure of every
        # implement task in this plan re-emits as `Error Message:` followed by nothing).
        $block = @()
        $inBlock = $false
        foreach ($line in ($out -split "`r?`n")) {
            if ($line -match '^\s*Failed\s+\S') { $inBlock = $true }
            elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
            if ($inBlock) { $block += $line }
        }
        if ($block.Count -eq 0) {
            $block = @("(no per-test failure block for '$($run.Filter)' - the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)")
        }
        $details += "--- $($run.Filter) ---"
        $details += $block
        continue
    }

    $passed = 0
    $failed = 0
    if ($out -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
    if ($out -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
    if (($passed + $failed) -lt 1) {
        $problems += "ZERO-MATCH: the filter '$($run.Filter)' executed no tests in $($run.Project) — that exits 0 and would certify this task on an empty set."
    }
}

if ($problems.Count -gt 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END for the ~60-line retry tail, #179) ==="
    $details | ForEach-Object { Write-Output $_ }
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}

exit 0
