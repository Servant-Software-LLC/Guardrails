# catches: a test file that PASSES against the throwing stubs - i.e. asserts nothing real (a tautology),
#          or the agent implemented the behaviour instead of stubbing it. With the build already green
#          (01), a non-zero test exit here unambiguously means the tests RAN and FAILED = TDD red.
#          SCOPED TO THIS PAIR'S OWN TEST CLASS (#455). The bare plan-wide `Category=ModelTieringStage1`
#          trait selects EVERY Stage 1 test across all five classes, so this red proof was satisfied by ANY
#          sibling pair's intended-red tests whether or not THIS pair's tests failed - a silent tautology
#          decided by merge order, not by correctness.
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)
$filter = 'Category=ModelTieringStage1&FullyQualifiedName~PromptRunnerSchemaTests'
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo 2>&1
$code = $LASTEXITCODE
$log | ForEach-Object { Write-Output $_ }

# GUARD FIRST on this INVERSE check (#455) - deliberately the opposite order from the forward
# `tests-pass` form. Here a crashed or never-started test host also exits NON-ZERO, which is this
# check's SUCCESS condition, so a guard placed second would certify "TDD red" over a run that executed
# nothing. Key on the EXECUTED count, Passed+Failed: "Total:" also counts [Skip]ped tests, so a class of
# [Fact(Skip=...)] would report a match while running zero. Never key on "No test matches the given
# testcase filter" - that string is verbosity-dependent and was measured NOT to fire (#248).
$ran = ([regex]::Matches(($log | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "ZERO tests executed - the TDD-red proof certified NOTHING. The --filter '$filter' matched no tests, is malformed, every matched test is [Skip]ped, or the test host failed to start (read the log above). This is NOT a tautology finding: do NOT rewrite the tests."
    exit 1
}

if ($code -eq 0) {
    Write-Output "The PromptRunnerSchemaTests tests PASS against the NotImplementedException stubs - they assert nothing real. Encode the plan's behaviours so they fail before the implementation lands."
    exit 1
}
exit 0
