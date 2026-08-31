# catches: a regression in Guardrails.Integration.Tests, and the second half of the section 13 zero-edits
#          claim: `tests/Guardrails.Integration.Tests/RetrySalvageTests.cs` - which pins the literal
#          heading "## Prior attempt work is salvageable", the ref name, and the protected-artifact
#          suppression - must still pass UNTOUCHED after stage 2 threads a defaulted SalvageFraming
#          through AppendSalvageSection and a restrictToScope filter through PreserveAttemptToRef. That
#          suite calls PreserveAttemptToRef DIRECTLY, so an implementation that made restrictToScope
#          required, or that filtered on the null path, breaks here and nowhere else.
#
#          It also catches the divergence-3 regression in the other direction: RetrySalvageTests
#          exercises the RETRY path, which must pass restrictToScope: null and stay byte-identical to
#          today (plan 31 section 3.4 divergence 3). A stage-2 implementation that filtered the retry path too
#          would strip out-of-scope hunks the retry path is documented to keep, and this is the suite
#          that notices.
#
# LOCAL - no `scope` key (#165), same terminal-postcondition reasoning as 02.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# NO -v q on a TEST command (#462/#179).
$out = dotnet test tests/Guardrails.Integration.Tests --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST on a forward check (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the Integration suite is red on the merged HEAD. If the failures are in RetrySalvageTests, stage 2 changed the RETRY path - it must pass restrictToScope: null and stay byte-identical (plan 31 section 3.4 divergence 3); fix the implementation, never the suite."
    exit 1
}

# ZERO-MATCH GUARD (#455): EXECUTED count, never 'Total:' - this project carries 4 [Skip]ped tests, so
# a Total-keyed guard would be cleared by a run that executed nothing at all.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed in tests/Guardrails.Integration.Tests - the terminal gate certified nothing. The test host did not run."
    exit 1
}
exit 0
