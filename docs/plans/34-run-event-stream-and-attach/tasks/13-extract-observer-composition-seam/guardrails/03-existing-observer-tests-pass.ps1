# catches: a GUTTED extraction. The source-shape check beside this one certifies that BuildObserverChain
#          is DECLARED and CALLED by both branches - vocabulary and structure. It cannot see whether the
#          method still BUILDS THE SAME CHAIN: a body that simply `return inner;` satisfies every clause
#          of it while destroying the observer composition, and this task authors no tests of its own.
#          These PRE-EXISTING tests drive the log-site observer, so a gutted extraction reds HERE, at the
#          task that caused it, instead of misattributing to task 14/15 or the terminal gate.
#          Re-emits the failure DETAIL at the END so it reaches the retry-feedback tail (#179).
# LOCAL (no scope key): a regression postcondition about THIS task's own segment, not a union invariant.
# NOT Category=RunEvents - these are pre-existing tests this plan does not author, which is the point.
# They are OUTSIDE this task's writeScope by design: a pure extraction must not need to edit them, and
# the change cannot shift any golden they pin (no behaviour change), so #193's orphan trap does not apply.
# Measured baseline (#478): n/a - exit-code + executed-count check, no required-present clause.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard reads is LOCALIZED (#455)
$filter = 'FullyQualifiedName~LogSite'
# NO -v q on the TEST command: it deletes exactly the failure block the re-emit below looks for (#462).
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the pre-existing log-site observer tests regressed - BuildObserverChain no longer builds the chain it replaced (see failure details above)"
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests or is malformed; the pre-existing log-site tests are the regression cover for this extraction."
    exit 1
}
exit 0
