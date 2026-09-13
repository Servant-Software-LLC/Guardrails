# catches: building this plan on an ALREADY-RED area. Every task here modifies code the
#          integration suite covers, so a pre-existing failure would be misattributed to the
#          first task that runs, burn its retries, and surface as a late needs-human.
#          "Never build on red" (#181), asserted once before the DAG.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Localized summary line defeats the count guard on a non-en box (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# EXCLUDES this plan's own not-yet-authored tests. This is the ONE place the plan-wide
# trait belongs (#455) — the != exclusion, never a task-level filter.
$out = & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo --filter "Category!=Supply" 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== The integration area is ALREADY RED before this plan runs ==="
    # #179: re-emit the failure detail at the END so the WHY reaches the halt feedback,
    # not just [FAIL] <name>.
    # #608: a BLOCK capture, never a line allowlist. MEASURED against real xunit.v3 + VSTest
    # output: an allowlist drops the `Failed <TestName>` header (so the detail names no test)
    # and `System.NotImplementedException : ...` (so the dominant first-attempt failure of every
    # implement task in this plan re-emits as `Error Message:` followed by nothing).
    $inBlock = $false
    $emitted = 0
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S') { $inBlock = $true }
        elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
        if ($inBlock) { Write-Output $line; $emitted++ }
    }
    if ($emitted -eq 0) {
        Write-Output "(no per-test failure block in the runner output - the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)"
    }
    Write-Output ""
    Write-Output "Fix the pre-existing breakage before this plan builds on it — no task here can."
    exit 1
}

exit 0
