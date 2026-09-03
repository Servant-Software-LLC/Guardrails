# catches: an implementation whose behavior deviates from the tests THIS task pair owns. The --filter
#          names this pair's OWN test class, never the plan-wide trait alone - a trait-only filter
#          asserts the state of every test in the plan, so this task could not go green until a task
#          that DEPENDS on it had run (a deadlock validate/graph --check cannot see, #455).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
# Measured baseline (#478): n/a - exit-code + executed-count check, no required-present clause.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=RunEvents&FullyQualifiedName~TaskExecutorAttemptCompletionTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "TaskExecutorAttemptCompletionTests failing - TaskExecutor does not raise AttemptFinished on every completion path with the journaled outcome (see failure details above)"
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the tests this task pair actually owns."
    exit 1
}
exit 0
