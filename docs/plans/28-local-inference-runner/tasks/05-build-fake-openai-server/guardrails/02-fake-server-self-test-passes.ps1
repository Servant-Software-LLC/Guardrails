# catches: a scripted loopback server that compiles but cannot actually be driven - a fixture nobody ever
#          ran. Three later task pairs (08/09/10/11, 18/19, 20/21) build every one of their assertions on
#          this server, so a broken fixture surfaces as a wall of failures in tasks that did nothing wrong
#          and cannot fix it (their writeScope excludes this file).
#
# This is a REAL test over a REAL loopback socket, not a source grep for the scripted-behaviour surface.
# A grep would measure vocabulary, not capability (#468) - and the whole point of authoring the server
# before the runner is that it already misbehaves on demand.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
$filter = 'FullyQualifiedName~FakeOpenAiServerTests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failure detail (re-emitted at the END so the WHY reaches the retry feedback, #179) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*(\[FAIL\]|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "The fake server's own self-test is failing. Every later runner, preflight and providers-check task drives this fixture - fix it here, before three task pairs inherit the breakage."
    exit 1
}

$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The self-test class was never authored, so the fixture is unproven - an exit code of 0 over an empty set certifies nothing."
    exit 1
}

Write-Output "Fake server drivable: $executed self-test(s) executed, 0 failed."
exit 0
