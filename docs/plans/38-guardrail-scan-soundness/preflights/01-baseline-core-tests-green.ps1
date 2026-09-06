# catches: a brownfield plan building on a RED base - the EXISTING Guardrails.Core.Tests are already
#          failing on the starting code. Asserting them green BEFORE the DAG means a later work task's
#          tests-pass failure is attributable to THAT task, not to pre-existing breakage, and this
#          plan's new TDD red is unambiguous (#181). Re-emits the failure DETAIL at the END so a red
#          baseline's WHY reaches the halt feedback, not just `[FAIL] <name>` (#179, dotnet.md 4.2).
# Scoped to the touched area's EXISTING tests via --filter, NEVER a whole-project run: this plan's own
# tests carry Category=ScanSoundness and do not exist yet, so they are excluded here. This `!=` is the
# ONE place the plan-wide trait stands ALONE (#455) - every task-level filter also names its own class.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)

$out = dotnet test tests/Guardrails.Core.Tests --filter "Category!=ScanSoundness" --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = @()
    $emit = $false
    foreach ($line in $out) {                              # BLOCK capture, not a line allowlist (#608)
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the halt feedback) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    Write-Output "the existing tests in tests/Guardrails.Core.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter 'Category!=ScanSoundness' matched no tests, is malformed, or every matched test is [Skip]ped. A vacuous baseline is worse than none (#181)."
    exit 1
}
exit 0
