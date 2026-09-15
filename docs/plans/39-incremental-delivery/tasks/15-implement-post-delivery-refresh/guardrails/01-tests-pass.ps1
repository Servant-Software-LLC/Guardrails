# catches: an implementation that does not satisfy the tests authored upstream, or that breaks tests an
#          EARLIER task already turned green in the code this task edits — and, via a per-class zero-match
#          guard read from the runner's TRX, a filter clause that silently selects nothing (#455).
#
#          TWO dotnet test runs, both required, covering THREE classes (reviews 2026-09-13):
#            - Guardrails.Integration.Tests: this task's own PostDeliveryRefreshTests, plus task 07's
#              WaveBarrierDeliveryTests. This task edits Scheduler.BuildGateHalt, which task 08's trial-gate
#              halt runs through (round 5, d39-trial-gate-failure). A rebuilt headline that dropped 08's
#              trial disclosure used to pass every guardrail on this task.
#            - Guardrails.Core.Tests: task 28's WaveDeliveryWiringTests, which drive the same barrier
#              delivery over a recording provider. That class lives in the OTHER test project, so it needs
#              its own dotnet test.
#          Without them, such a regression surfaced only at the plan-root terminal gate, where no task may
#          edit tests. An 'A|B' filter's summary counts cannot show that ONE of its classes matched nothing,
#          so each class's presence is read from the TRX.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$runs = @(
    @{ Project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'; Classes = @('PostDeliveryRefreshTests', 'WaveBarrierDeliveryTests') },
    @{ Project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'; Classes = @('WaveDeliveryWiringTests') }
)

$problems = @()
$details = @()
foreach ($run in $runs) {
    $filter = ($run.Classes | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|'
    $results = Join-Path $env:TEMP ('gr39-tests-pass-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    try {
        $out = & dotnet test $run.Project -c Debug --nologo --filter $filter `
            --logger 'trx;LogFileName=tests-pass.trx' --results-directory $results 2>&1 | Out-String
        $code = $LASTEXITCODE

        Write-Output "=== dotnet test $($run.Project) --filter $filter (exit $code) ==="
        Write-Output $out

        # Exit-code check FIRST on the forward polarity: a test host that never ran must not be misreported
        # as a bad filter.
        if ($code -ne 0) {
            $problems += "FAILED: '$filter' in $($run.Project) exited $code."
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
                $block = @("(no per-test failure block for '$filter' - the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)")
            }
            $details += "--- $($run.Project) ---"
            $details += $block
        }

        # Per-class zero-match: the TRX names every executed test, so a clause that selected nothing is
        # visible even when its sibling clause ran tests.
        $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
        if ($trx) {
            [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
            $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })
            foreach ($class in $run.Classes) {
                if (@($nodes | Where-Object { $_.testName -like ('*.' + $class + '.*') }).Count -lt 1) {
                    $problems += "ZERO-MATCH: '$class' executed no tests in $($run.Project) — a class that selects nothing would certify this task on an empty set."
                }
            }
        }
        elseif ($code -eq 0) {
            $problems += "PRECONDITION: '$filter' in $($run.Project) exited 0 but wrote no TRX, so which classes ran cannot be checked."
        }
    }
    finally {
        Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
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
