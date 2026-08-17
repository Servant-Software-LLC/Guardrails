# catches: a classification change whose behaviour deviates from the tests THIS task pair owns, and
#          - via the EXISTING ClaudeSignalClassifierTests included in the same filter - an extension
#          so greedy it reclassifies a signal the quarantine already pins. That second half is the
#          expensive direction: a phrase broad enough to catch an ordinary error would make a real
#          logic failure ride the #115 pause instead of consuming its retry budget, so the task
#          never surfaces and the run stalls on a wall no backoff can clear.
#          Two classes, so no bare plan-wide trait (#455): the existing class carries no Category
#          trait at all, which is why the filter is an FQN alternation with NO trait term.
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'FullyQualifiedName~ConnectionUnavailabilityClassificationTests|FullyQualifiedName~ClaudeSignalClassifierTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "connection-unavailability classification is not to DoR 6.3. If the FAILING test is in ClaudeSignalClassifierTests (the pre-existing class), your new signals are too greedy and have reclassified an already-pinned shape - make them longer and more discriminating rather than deleting the pin (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter matching nothing, or malformed, also exits 0. Two classes must
# each contribute; keyed on the EXECUTED count (Passed+Failed), since "Total:" counts [Skip]ped.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 2) {
    Write-Output "exit 0 but only $ran test(s) executed - this guardrail certified nothing. The --filter '$filter' matched fewer tests than the two classes it names, is malformed, or the matched tests are [Skip]ped."
    exit 1
}
exit 0
