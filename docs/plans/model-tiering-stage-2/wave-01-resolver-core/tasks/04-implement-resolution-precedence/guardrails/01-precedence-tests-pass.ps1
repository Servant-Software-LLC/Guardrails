# catches: a precedence implementation whose behavior deviates from the tests THIS task pair owns.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone (#455).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# BOTH wave-1 classes. This task edits the same TierResolver.cs/TierResolution.cs that task 02
# implemented, so a Resolve refactor that breaks SelectCandidate would otherwise go GREEN here and
# only red-halt at the wave EXIT GATE - which is a gate, not a task: no retry budget, and the failure
# is attributed to the wave instead of to the task that caused it.
# This does NOT reintroduce #455: the selection tests are made green by task 02, an ANCESTOR of this
# task, so there is no downstream dependency to deadlock on and no concurrent sibling to race.
$filter = 'Category=TierResolution&(FullyQualifiedName~TierResolverPrecedenceTests|FullyQualifiedName~TierResolverCandidateSelectionTests)'
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
    Write-Output "TierResolverPrecedenceTests failing - precedence is not implemented to DoR 6.1 (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter matching nothing, or malformed, also exits 0.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}
exit 0
