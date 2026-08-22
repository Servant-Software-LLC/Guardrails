# catches: a wave-1 merge that compiles but REGRESSES an existing Core test. Every task guardrail in
#          this wave is --filter-scoped to its own test class (#455), so by construction none of them
#          can see a regression elsewhere in Guardrails.Core.Tests - three tasks edit PlanValidator.cs,
#          which ~1900 existing tests exercise. This is the UNFILTERED suite on the merged wave HEAD,
#          and it is the only check in the wave that can observe collateral damage.
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179, §4.2).
# LOCAL - no `scope` key (GR2059 / #459), and doubly so here: a whole-suite run is a TERMINAL
# postcondition, which at integration scope would red-halt every correct partial merge in which a
# downstream task has not yet run (#125/#165). It belongs exactly where it is - once, at the wave exit.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)
# UNFILTERED on purpose: this is the one gate whose job is everything the task filters exclude.
# NO -v q on a TEST command (#179).
$out = dotnet test tests/Guardrails.Core.Tests --nologo 2>&1
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
    Write-Output "the merged wave-1 HEAD fails Guardrails.Core.Tests - a task-level filter cannot see this, so the regression is collateral damage from one of the three PlanValidator.cs edits"
    exit 1
}

# ZERO-MATCH GUARD (#455): an unfiltered run cannot mis-select, but a test host that never started
# exits 0 in the malformed case and prints no summary - so a suite that executed NOTHING would
# otherwise certify the wave green.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this gate certified nothing. The Guardrails.Core.Tests host did not run; inspect the log above."
    exit 1
}
exit 0
