# catches: a merged HEAD whose suite is red. Every task's own filtered tests passed in its own
#          segment; this is the only check that runs the WHOLE suite on the merged result, so it is
#          the only one that can see a regression one task's change caused in another's tests.
#          LOCAL, not scope:"integration" (#165) - a whole suite is a terminal POSTCONDITION, and at
#          an intermediate union this plan's TDD test files reference types whose implement task has
#          not run yet.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# No -v q on a test command: it deletes the Error Message/Expected/Actual/Stack Trace block the
# re-emit below exists to put in the retry tail, defeating #179 by the flag alone (#462).
$out  = & dotnet test Guardrails.sln -c Debug --nologo 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Whole-suite FAILURE detail (re-emitted for the retry tail, #179) ==="
    # #608: a BLOCK capture, never a line allowlist. An allowlist drops the `Failed <TestName>`
    # header and `System.NotImplementedException : ...` - the dominant first-attempt failure of
    # every implement task in this plan - leaving `Error Message:` followed by nothing.
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
    exit 1
}
exit 0
