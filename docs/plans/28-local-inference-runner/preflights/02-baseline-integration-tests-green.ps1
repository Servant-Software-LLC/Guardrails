# catches: a plan that builds on ALREADY-RED existing tests in Guardrails.Integration.Tests. The runner,
#          the kind-aware splice, the endpoint preflight and `providers check` are all verified there
#          against a real loopback server, so a task's tests-pass guardrail would inherit pre-existing
#          breakage it cannot fix - misattributed to the task, burning its retry budget and ending at
#          needs-human with its own deliverable complete. "Never build on red" (#181).
#
# SCOPE: the EXISTING Integration tests only, via an FQN exclusion of every test class THIS plan authors -
#        never a whole-project run (the #165/#176 compile-coupling trap). The FQN form is deliberate over a
#        shared plan-wide trait: plan 27 measured the trait misattributing a sibling's red tests here.
#
# Required-present baseline (#478): a POSITIVE precondition on the STARTING tree, so green-on-arrival BY
#        DESIGN - the class Step 7.0a exempts. Measured at authoring time: 945 executed, 0 failed,
#        4 skipped (the skips are pre-existing opt-in real-Claude and no-routing golden tests).
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED; pin the culture before the run or the executed-count guard inverts (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this preflight is scoped to the Integration test project and cannot run without it."
    exit 1
}

$filter = 'FullyQualifiedName!~OpenAiCompatTransportTests' +
          '&FullyQualifiedName!~OpenAiCompatToolLoopTests' +
          '&FullyQualifiedName!~OpenAiCompatVerdictTests' +
          '&FullyQualifiedName!~KindAwareHarnessTests' +
          '&FullyQualifiedName!~OpenAiCompatPreflightTests' +
          '&FullyQualifiedName!~ProvidersCheckTests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): EXECUTED count (Passed + Failed), not 'Total:' - which counts [Skip]ped tests,
# and this project genuinely has 4 skips, so a Total-keyed guard would be satisfied by skips alone.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "BASELINE FILTER MATCHED NOTHING: 0 tests executed in $project. The exclusion filter is wrong or the test host never ran - this preflight is certifying nothing. Fix the filter before running the plan."
    exit 1
}

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Pre-existing failures in Guardrails.Integration.Tests (detail re-emitted) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The Integration area's EXISTING tests ($executed executed, $failed failed) are already failing on the starting code. Fix the pre-existing breakage before this plan builds on it."
    exit 1
}

Write-Output "Baseline green: $executed existing Integration tests executed, 0 failed."
exit 0
