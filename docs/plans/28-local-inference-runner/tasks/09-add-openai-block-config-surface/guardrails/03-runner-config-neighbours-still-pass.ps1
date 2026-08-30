# catches: a change that makes THIS task's own tests pass while breaking a neighbouring suite it had no
#          business touching. Scoped to the existing prompt-runner schema suite, which this task edits shared code in - not the whole
#          repo (that is the terminal gate's job, once, on the merged HEAD).
#
# Green-on-arrival BY DESIGN - this is a `tests-untouched` regression check, the class Step 7.0a
# exempts. Measured at authoring time: the filter selects 52 existing tests, all passing.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
$filter = 'FullyQualifiedName~PromptRunner'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Neighbour failures (detail re-emitted at the END, #179) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*(\[FAIL\]|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "This task broke the existing prompt-runner schema suite ($failed failed of $executed). Its own tests passing is not enough - fix the regression."
    exit 1
}

if ($executed -lt 1) {
    Write-Output "NEIGHBOUR FILTER MATCHED NOTHING: 0 tests executed for '$filter'. This check is certifying nothing."
    exit 1
}

Write-Output "Neighbours intact: $executed executed in the existing prompt-runner schema suite, 0 failed."
exit 0
