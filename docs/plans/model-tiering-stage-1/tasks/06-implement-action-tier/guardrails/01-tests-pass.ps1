# catches: an implementation that does not satisfy the authored tier tests.
#          SCOPED TO THIS TASK'S OWN TEST CLASS (#455). The bare plan-wide `Category=ModelTieringStage1`
#          trait selects EVERY Stage 1 test across all five classes, so this check could not go green
#          until sibling pairs whose implementation tasks are NOT ancestors of this one had landed - a
#          deadlock between a task and a sibling's test corpus that neither `validate` nor `graph --check`
#          can see, because it is not an edge in the DAG. A task guardrail may only assert what THIS
#          task can fix.
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (the last ~60 lines of stdout) - the tail would otherwise show WHAT failed, not WHY (#179).
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)
# `~ActionTierTests` and not `~ActionTier`: FullyQualifiedName~ is a SUBSTRING match, and the broader
# spelling would also select the sibling ActionTierProvenanceTests class (#193). Copied VERBATIM from
# this pair's 05-author-tests-action-tier/guardrails/02-tests-fail-on-stubs.ps1 - the two halves of a
# TDD pair must never drift apart.
$filter = 'Category=ModelTieringStage1&FullyQualifiedName~ActionTierTests'
# No verbosity flag on the TEST command (#462): quiet verbosity suppresses the whole
# Error Message / Expected / Actual / Stack Trace block, leaving only "[FAIL] <name>" for the re-emit
# below to find - which defeats #179 by the flag alone. Quiet belongs on `dotnet build`, not here.
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, zero-match guard second (#455). A test host that never ran exits NON-zero and prints
# no summary at all, so a guard placed first would swallow it and confidently misdiagnose "the filter
# matched ZERO tests - was the class renamed?" - pointing the retry at the one thing it IS allowed to
# change (the test class), which would then break this pair's other half too.
if ($code -ne 0) {
    $detail = $log |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40          # bound the block so it fits the ~60-line feedback tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "The action.tier tests still fail - fix the implementation, not the tests."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count, Passed+Failed: "Total:" also counts [Skip]ped
# tests, so a [Fact(Skip=...)] class would satisfy a Total:-keyed guard having run nothing. Never key on
# "No test matches the given testcase filter" - that string is verbosity-dependent (#248).
$ran = ([regex]::Matches(($log | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified NOTHING. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Was ActionTierTests renamed, or its Category trait dropped?"
    exit 1
}
exit 0
