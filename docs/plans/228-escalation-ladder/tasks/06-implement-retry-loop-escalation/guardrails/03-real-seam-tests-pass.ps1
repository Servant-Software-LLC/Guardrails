# catches: the passing-but-blind case (#382) - an escalation ladder that is unit-green and never runs.
#          EscalationLadder's own tests construct a TierResolution by hand and call Apply directly; they
#          go green over an executor that resolves the route and then throws the ladder's answer away,
#          over a counter that increments on the wrong outcome, and over a BuildProvenance that never
#          records escalatedFrom. This guardrail runs the tests that drive the REAL TaskExecutor retry
#          loop through the real PlanLoader/Scheduler and assert on the JOURNAL - bytes only a
#          production route resolution, threaded into BuildProvenance and recorded, could have written.
#          It also catches the over-broad trigger: a timeout must NOT escalate.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone - a trait-only
#          filter asserts the state of every test in the plan, so this task could not go green until a
#          task that DEPENDS on it has run (a deadlock validate/graph --check cannot see, #455).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
# scope: LOCAL (no scope key). A real-seam proof asserts "this component works through the real seam",
#          which cannot be true before THIS task's own action has run - so it fails the #125 union-safe
#          test and must not be tagged scope:"integration" (the #250 mistake).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=EscalationLadder&FullyQualifiedName~RetryLoopEscalationTests'   # verbatim from task 05's census
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
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
    $detail = $detail | Select-Object -First 40            # bound so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    Write-Output "RetryLoopEscalationTests failing - the ladder is not reached through the real retry loop, or a timeout escalated when it must not (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this real-seam proof certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against RetryLoopEscalationTests, the class task 05 authored."
    exit 1
}
exit 0
