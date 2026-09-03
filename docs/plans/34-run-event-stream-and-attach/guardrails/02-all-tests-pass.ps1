# catches: a merged plan branch whose whole suite is red - a regression one task introduced into another
#          task's area, which no per-task filtered guardrail can see because each names only its own class.
# LOCAL (no scope key): a whole-suite run is a TERMINAL postcondition, not a union invariant (#125/#165).
# Re-emits the failure DETAIL at the END so the WHY reaches the halt feedback (#179, dotnet.md 4.2).
# Measured baseline (#478): n/a - exit-code + executed-count check, no required-present clause.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard reads is LOCALIZED (#455)
# NO -v q on the TEST command: it deletes exactly the failure block the re-emit below looks for.
$out = dotnet test Guardrails.sln --nologo 2>&1
$testExit = $LASTEXITCODE
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
    Write-Output "the full suite is failing on the merged plan branch (see failure details above)"
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this terminal gate certified nothing."
    exit 1
}
exit 0
