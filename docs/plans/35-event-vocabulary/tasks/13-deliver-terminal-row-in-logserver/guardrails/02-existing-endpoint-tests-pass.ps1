# catches: a "fix" that held the connection open for a missing events.jsonl. That change
#          would make the shipped empty-200 test HANG rather than fail - the worst kind of regression,
#          because it reads as a timeout rather than as a broken contract.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = "FullyQualifiedName~EventsEndpointTests"
# --blame-hang-timeout: the plan states in three places that the WRONG change here (holding the
# connection open for a missing events.jsonl) makes EventsEndpoint_OnAMissingEventsFile_... HANG rather
# than fail, because it reads the whole body. Without a bound, that wrong implementation burns the full
# task timeout on every attempt and returns NO feedback at all - strictly worse retry input than a
# failure. The bound converts a hang into a named, actionable failure.
$log = & dotnet test tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj --filter $filter --nologo --blame-hang-timeout 90s 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

# Forward polarity: exit code FIRST (so a test host that never ran is not misreported as a bad
# filter), then the zero-match guard on the EXECUTED count - never Total, which counts [Skip]ped.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failures ==="
    $log -split "`r?`n" | Where-Object {
        $_ -match '^\s*(Error Message|Expected|Actual|Stack Trace|\s+at )' -or $_ -match '\[FAIL\]'
    } | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The tests authored for this deliverable still fail. The assertion detail above is the WHY."
    exit 1
}

$passed = 0; $failed = 0
if ($log -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests - it exits 0 while proving nothing. The class was renamed or never authored."
    exit 1
}
Write-Output "All $passed test(s) pass for EventsEndpointTests."
exit 0
