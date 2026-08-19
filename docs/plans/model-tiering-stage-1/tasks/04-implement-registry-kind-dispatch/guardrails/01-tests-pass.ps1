# catches: a dispatch implementation that does not satisfy the authored tests.
#          SCOPED TO THIS TASK'S OWN TEST CLASS. The bare plan-wide `Category=ModelTieringStage1`
#          trait selects EVERY Stage 1 test across all six task pairs, which broke this two ways: a
#          tests-pass guardrail deadlocked behind a sibling's INTENDED-RED tests that only a DOWNSTREAM
#          task fixes, and a tests-fail-on-stubs guardrail was satisfied by ANY sibling's red tests
#          instead of its own. A task guardrail may only assert what THIS task can fix.
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (the last ~60 lines of stdout) - the tail would otherwise show WHAT failed, not WHY (#179).
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)
$filter = 'Category=ModelTieringStage1&FullyQualifiedName~RegistryKindDispatchTests'
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
    Write-Output "The registry dispatch tests still fail - fix the implementation, not the tests."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count, Passed+Failed: "Total:" also counts [Skip]ped
# tests, so a [Fact(Skip=...)] class would satisfy a Total:-keyed guard having run nothing. Never key on
# "No test matches the given testcase filter" - that string is verbosity-dependent (#248).
$ran = ([regex]::Matches(($log | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified NOTHING. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Was RegistryKindDispatchTests renamed, or its Category trait dropped?"
    exit 1
}
exit 0
