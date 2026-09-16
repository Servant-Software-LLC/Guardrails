# catches: building this plan on an ALREADY-RED area. Ten implement tasks here modify code the
#          Core suite covers (Overwatch*, Scheduler, SuppliedDrain, RunOutcomePolicy, IRunObserver),
#          so a pre-existing failure would be misattributed to the first task that runs, burn its
#          retries, and surface as a late needs-human (#181).
#          The zero-match guard is not decoration: a typo'd or malformed --filter exits 0, which
#          would certify "the area is green" over a run that executed nothing.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the summary line the guard below reads is LOCALIZED (#455)

# The plan-wide trait stands ALONE in exactly ONE place - this != exclusion (#455). Every
# task-level filter in this plan names its own pair's test class instead. "OverwatchSupply" is a
# NEW trait value: the existing Supply-area tests carry Category="Supply" (11 files), and
# Category!= is an EXACT match, so those 11 still run in this baseline.
$out  = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo --filter "Category!=OverwatchSupply" 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# EXIT CODE FIRST on a forward check (#455): a test host that never started exits NON-zero with no
# summary at all, so checking the code first reports its real error instead of blaming the filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== The Core area is ALREADY RED before this plan runs ==="
    # #608: a BLOCK capture, never a line allowlist - an allowlist drops the `Failed <TestName>`
    # header and the exception payload, which is most of the diagnosis.
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
    Write-Output "Fix the pre-existing breakage before this plan builds on it - no task here can."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean the area is green. Key on the EXECUTED count
# (Passed + Failed); "Total:" would also count [Skip]ped tests, so a fully-skipped run would pass.
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The filter 'Category!=OverwatchSupply' matched no tests, is malformed, or every match is [Skip]ped."
    exit 1
}

Write-Output "Core baseline green: $ran test(s) executed, all passing."
exit 0
