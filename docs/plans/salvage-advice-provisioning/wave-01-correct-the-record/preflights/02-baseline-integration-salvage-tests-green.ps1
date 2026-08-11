# catches: starting from RED - the pre-existing RetrySalvageTests are already failing before this plan
#          touches the retry-salvage code they cover, so a later task's tests-pass failure would be
#          misattributed to that task.
$log = dotnet test tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~RetrySalvageTests" 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -eq 0) { exit 0 }

# A non-zero exit does NOT always mean a test failed. xUnit reports a class-fixture/cleanup fault
# (e.g. "Test Class Cleanup Failure ... TestPipelineException", issue #433) as a non-zero exit while
# reporting "Failed: 0" - and a run can also die from a host/crash with no summary at all. Diagnose
# which happened, so the halt message names the REAL cause instead of blaming healthy tests.
$failedCount = $null
if ($log -match 'Failed:\s*(\d+)') { $failedCount = [int]$Matches[1] }

Write-Output ""
Write-Output "---- baseline failure detail (why) ----"
foreach ($line in ($log -split "`r?`n")) {
    if ($line -match 'error|Assert\.|Exception|\[FAIL\]|Cleanup Failure|at Guardrails') { Write-Output $line }
}

if ($failedCount -eq 0) {
    Write-Output "the test run exited $code with NO failing tests (Failed: 0) - this is NOT pre-existing test breakage. Something outside the assertions failed: a class-fixture/cleanup fault (see issue #433), or the runner/host itself. Investigate the cleanup or host error above; do not 'fix' the tests."
} elseif ($null -eq $failedCount) {
    Write-Output "the test run exited $code and produced NO test summary at all - the runner or host failed before reporting. This is NOT evidence that the tests are red; investigate the error above."
} else {
    Write-Output "$failedCount existing RetrySalvageTests test(s) are ALREADY failing on the starting code - fix that pre-existing breakage before this plan builds on it"
}
exit 1
