# catches: a fix that made the round-trip green by breaking the shipped attach behaviour -
#          the finished-run replay, the two-concurrent-watchers guarantee, or the skip-unknown-members
#          forward compatibility the new line depends on.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = "FullyQualifiedName~AttachReplayTests"
$log = & dotnet test tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj --filter $filter --nologo 2>&1 | Out-String
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
Write-Output "All $passed test(s) pass for AttachReplayTests."
exit 0
