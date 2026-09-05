# catches: a regression in the EXISTING suite that no per-task filtered guardrail can see - this plan
#          adds a value to the TierSource enum and a field to AttemptProvenance, both of which are read
#          by journal round-trip, telemetry-ingest, dry-run and observer-projection tests that no task in
#          this plan filters on. It also catches a merged HEAD on which this plan's own new tests pass
#          only in isolation. Re-emits the failure DETAIL at the END so the WHY reaches the retry-feedback
#          tail (#179, stacks/dotnet.md 4.2 BLOCK capture, #608).
#          LOCAL (no scope key, #165): a whole suite is a TERMINAL postcondition. At an intermediate union
#          this plan's test files reference members a downstream task has not written yet, so an
#          integration-scoped copy would fail there and roll a correct wave back.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test Guardrails.sln --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the saved output)

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary at all,
# so checking the exit code first reports its real error instead of blaming the run.
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
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    Write-Output "the full Guardrails.sln suite is failing on the merged plan-branch HEAD (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a run that executed nothing also
# exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count [Skip]ped tests), never on
# a verbosity-dependent string (#248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this terminal gate certified nothing. The test host produced no summary line; read the full log above."
    exit 1
}
exit 0
