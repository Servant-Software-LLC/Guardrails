# catches: a regression anywhere in Guardrails.Integration.Tests - which for this plan means the pins that
#          cannot be faked at all. Section 8: "#382's lesson is that a fake-masked unit guardrail certifies
#          green while the real composition-root path is broken, and the default execution mode for a real
#          run is worktree mode." P2 (the worktree write sites), P3 (the trailer on a real git segment) and
#          P9 (milestone C's acceptance criterion: a green run with a mid-run edit does NOT deliver) all
#          live here.
#
#          It is also the last gate on the tripwire. AStrayDsStoreMidRun_'s Assert.True(report.AllSucceeded)
#          has now survived sixteen stages: stage 2 inverted the assertion three lines below it, stage 13
#          built the gate that could turn it red, stage 14 rewrote two of its siblings. Section 6.7 calls it
#          "the only thing standing between the delivery gate and being muted within a week", and this is
#          where it is read for the last time.
#
#          And it is where the advisory string is finally checked in company: stage 14 authored the
#          assertions, stage 15 wrote the text, and section 15.1's whole worry was a harness that prints
#          "Nothing was halted and nothing was re-run" beside exit 2 and a blocked delivery.
#
# LOCAL - no `scope` key (#165), same terminal-postcondition reasoning as 02.
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED - pin the culture before the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the re-emit
# below exists to surface, defeating #179 by the flag alone (#462).
$out = dotnet test tests/Guardrails.Integration.Tests --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero with no
# summary at all, so checking the exit code first reports its real error instead of blaming a filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Integration suite is red on the merged HEAD. If AStrayDsStoreMidRun_ is red, the delivery gate compares the FULL surface instead of the ignore-list-filtered one and will be disabled within a week (section 6.2). If TheRenderedText_ is red, the advisory still claims the POST-edit hash is recorded or still says nothing was halted - both false after this plan, on the exact surface it exists to make honest."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean tests passed - a run that executed nothing also
# exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count [Skip]ped tests, and
# tests/Guardrails.Integration.Tests carries skipped tests today.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed in tests/Guardrails.Integration.Tests - the terminal gate certified nothing. The test host did not run."
    exit 1
}
exit 0
