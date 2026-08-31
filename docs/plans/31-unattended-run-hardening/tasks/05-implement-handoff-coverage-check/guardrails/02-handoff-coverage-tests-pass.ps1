# catches: an implementation of the handoff-coverage check that does not satisfy the nine pins - and
#          in particular the two the whole issue turns on. Pins 1 and 2 are the two REAL plan-28 rows
#          in their broken state and both demand GR2069; pin 5a is red under the un-swapped argument
#          form (which can never match a glob); pin 4 demands the anchor discriminator hold BOTH ways
#          in one fixture; pins 6 and 7 assert the FULL diagnostic list is unchanged, so an
#          implementation that fires on a table-less plan or on prose cells breaks them.
#
#          Re-emits the assertion detail at the END so the WHY reaches the harness retry-feedback tail
#          (the last ~60 lines) - default `dotnet test` prints it mid-run and ends with only
#          "[FAIL] <name>" plus a count, which would tell the next attempt WHAT failed and not WHY (#179).
#
# The filter names THIS pair's OWN test class (#455). The plan introduces no plan-wide trait, so the
# class term stands alone - shape 3 of the four sanctioned forms, not an omission. It is
# discriminating: no other test class in this project contains 'HandoffScopeCoverageTests'.
$ErrorActionPreference = 'Continue'

# The run summary the zero-match guard reads is LOCALIZED - pin the culture FIRST (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'FullyQualifiedName~HandoffScopeCoverageTests'

# NO -v q on a TEST command: it deletes the Error Message / Expected / Actual / Stack Trace block the
# re-emit below exists to surface, defeating #179 by the flag alone (#462).
$out = & dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                      # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }      # full log first, for the attempt's saved output

# EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero with
# no summary at all. Guard-first would swallow that and report "the filter matched ZERO tests - check
# the class name", a confident misdiagnosis pointing at a file this task may not even edit.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "HandoffScopeCoverageTests is RED. If pins 1 or 2 fail expecting GR2069 and your check emitted GR2068, re-read plan 31 section 4.6: both plan-28 failures are SPLIT rows, not unreachable ones. If pin 5a fails, the glob arm's arguments are the wrong way round - IsInScope globs the SCOPE side, so a glob candidate needs IsInScope(entry, [candidate])."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean the pins passed - a --filter matching nothing, or
# a malformed one, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, so a fully-skipped class would clear a Total-keyed guard.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the --filter '$filter' matched nothing, is malformed, or every matched test is [Skip]ped. This guardrail certified nothing. The class is task 04's deliverable; if it is genuinely absent, escalate rather than writing it."
    exit 1
}
exit 0
