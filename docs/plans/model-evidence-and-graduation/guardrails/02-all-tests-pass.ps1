# catches: a plan whose per-task guardrails all passed but which BROKE something outside every task's
#          own filter - the whole point of a terminal gate. Each task-level tests-pass check is scoped to
#          its own test class (#455), so nothing before this point re-runs the suite the plan started
#          green on (the two baseline preflights). Unfiltered on purpose: this is the one place the FULL
#          suite belongs. LOCAL (no scope key) - a whole-suite run at an intermediate union would fail on
#          tests whose implementation task has not merged yet, red-halting a correct run (#125/#165).
#          Re-emits the failure detail at the END so the WHY reaches the halt output (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$failed = $false
foreach ($proj in @('tests/Guardrails.Core.Tests', 'tests/Guardrails.Integration.Tests')) {
    $out = dotnet test $proj --no-build --nologo 2>&1
    $testExit = $LASTEXITCODE
    Write-Output "=== $proj ==="
    $out | ForEach-Object { Write-Output $_ }
    if ($testExit -ne 0) {
        $failed = $true
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 25
        Write-Output ""
        Write-Output "=== Failure details for $proj (re-emitted for the halt output) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    }
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failed = $true
        Write-Output "$proj executed ZERO tests - the suite did not actually run, so this gate certified nothing."
    }
}
if ($failed) {
    Write-Output ""
    Write-Output "The full suite is not green on the merged plan HEAD (see the re-emitted details above)."
    exit 1
}
exit 0
