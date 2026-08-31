# catches: a regression this plan's changes introduced anywhere in Guardrails.Core.Tests, plus the ONE
#          claim section 13 stakes stages 2 and 3's test-free writeScopes on: that
#          `tests/Guardrails.Core.Tests/RetryPolicySalvageAdviceTests.cs` - which hard-pins
#          AppendSalvageSection's emitted bytes (the patch bullet FIRST, `git show "<ref>:<path>"`
#          verbatim, "EVERYTHING" banned, no git diff/git apply invocation) - still passes UNTOUCHED.
#          That suite is a CONTENT assertion, so source-compatibility does not prove it: it holds only
#          if SalvageFraming.Retry reproduces today's output byte-for-byte. If stage 2's framing
#          parameter moved the Retry branch's bytes, THIS is where it surfaces, and the answer is to
#          restore the bytes - never to edit the suite.
#
# LOCAL - no `scope` key (#165). A whole-suite run is a terminal postcondition: at an intermediate
#         union it selects a sibling task's intentionally-red TDD tests and red-halts a correct
#         partial merge. It runs once, here, on the fully-merged HEAD.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED - pin the culture before the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the
# re-emit below exists to surface, defeating #179 by the flag alone (#462).
$out = dotnet test tests/Guardrails.Core.Tests --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero with
# no summary at all, so checking the exit code first reports its real error instead of blaming a filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Core suite is red on the merged HEAD. If the failures are in RetryPolicySalvageAdviceTests, the Retry framing's BYTES moved (plan 31 section 3.3) - restore them; that suite must pass with zero edits and is what makes stages 2 and 3 legitimately test-free."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean tests passed - a run that executed nothing also
# exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed in tests/Guardrails.Core.Tests - the terminal gate certified nothing. The test host did not run."
    exit 1
}
exit 0
