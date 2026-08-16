# catches: a regression anywhere in the existing suite caused by this plan's changes, and any new
#          test that does not pass on the fully merged HEAD. The per-task filters each prove ONE
#          pair's tests; only this unfiltered run proves nothing else broke.
# LOCAL - no scope key (#165): a whole suite is a terminal postcondition (a mid-plan union holds
# tests for not-yet-implemented types), so running it at every union would red-halt a correct run.
# Re-emits the failure DETAIL at the END so the WHY reaches the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary is LOCALIZED
$out = dotnet test Guardrails.sln --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the full suite is not green on the merged plan-branch HEAD (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD: an unfiltered run should execute thousands of tests; zero means the host never ran.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the terminal gate certified nothing (the test host did not run)."
    exit 1
}
exit 0
