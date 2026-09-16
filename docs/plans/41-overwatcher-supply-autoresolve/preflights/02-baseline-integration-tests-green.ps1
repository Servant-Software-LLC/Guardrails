# catches: building this plan on an ALREADY-RED area. The wiring proof, the CLI forwarding guards
#          and the terminal-gate halt all live in the Integration suite, and three tasks modify
#          code it covers, so a pre-existing failure would be misattributed to the first task that
#          runs and surface as a late needs-human (#181).
#          The zero-match guard is not decoration: a typo'd or malformed --filter exits 0, which
#          would certify "the area is green" over a run that executed nothing.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the summary line the guard below reads is LOCALIZED (#455)

# The plan-wide trait stands ALONE in exactly ONE place - this != exclusion (#455).
$out  = & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo --filter "Category!=OverwatchSupply" 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# EXIT CODE FIRST on a forward check (#455) - see the Core baseline for why.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== The Integration area is ALREADY RED before this plan runs ==="
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

# ZERO-MATCH GUARD (#455) - executed count, never "Total:" (which counts [Skip]ped tests).
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The filter 'Category!=OverwatchSupply' matched no tests, is malformed, or every match is [Skip]ped."
    exit 1
}

Write-Output "Integration baseline green: $ran test(s) executed, all passing."
exit 0
