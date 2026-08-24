# catches: a wave-4 merge that compiles but REGRESSES an existing test. The task guardrail in this wave is
#          --filter-scoped to the two classes its own pair owns (#455), which leaves a real hole and this
#          wave's version of it is specific: 02-implement-models-used-report edits
#          RunCommand.PrintTotalCost, the method that prints the END of every run summary, and DryRunCliTests
#          already asserts on that summary's text (`Total prompt cost: $0.0150`, and the DoesNotContain
#          twin). A models-used line inserted in the wrong place, or one printed unconditionally, breaks
#          those shipped assertions and NO filter in this wave selects them. This is the UNFILTERED run of
#          BOTH suites on the merged wave HEAD, and it is the only check in the wave that can observe
#          collateral damage.
#
#          BOTH projects, not just Core: the models-used line is proven end-to-end from
#          Guardrails.Integration.Tests (it is the only test project that references Guardrails.Cli), so a
#          Core-only gate would leave the half that matters most unmeasured on the merged HEAD.
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179, dotnet.md 4.2).
# LOCAL - no `scope` key (GR2059 / #459), and doubly so here: a whole-suite run is a TERMINAL
# postcondition, which at integration scope would red-halt every correct partial merge in which a
# downstream task has not yet run (#125/#165). It belongs exactly where it is - once, at the wave exit.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)
$failures = @()

foreach ($proj in @('tests/Guardrails.Core.Tests', 'tests/Guardrails.Integration.Tests')) {
    # UNFILTERED on purpose: this is the one gate whose job is everything the task filter excludes.
    # NO -v q on a TEST command (#179).
    $out = dotnet test $proj --nologo 2>&1
    $testExit = $LASTEXITCODE                              # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40
        Write-Output ""
        Write-Output "=== $proj failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$proj FAILS on the merged wave-4 HEAD - a task-level filter cannot see this, so look first at collateral from the RunCommand.PrintTotalCost edit (DryRunCliTests asserts on that summary's text)"
        continue
    }

    # ZERO-MATCH GUARD (#455): an unfiltered run cannot mis-select, but a test host that never started
    # exits 0 in the malformed case and prints no summary - so a suite that executed NOTHING would
    # otherwise certify the wave green. Keyed on the EXECUTED count (Passed + Failed), never Total,
    # which counts [Skip]ped tests.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$proj exited 0 but executed ZERO tests - the host did not run, so this gate certified nothing for that project"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-4 suites: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
