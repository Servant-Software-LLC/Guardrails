# catches: test-author task that wrote the test files but forgot to publish their hashes
#          to state; the implementation task's tests-untouched guardrail reads these hashes —
#          if missing, that guardrail silently fails open (reads null from GUARDRAILS_STATE_IN)
$fragmentPath = $env:GUARDRAILS_STATE_FRAGMENT
if (-not $fragmentPath -or -not (Test-Path $fragmentPath)) {
  Write-Output "no state fragment written — action did not publish any state"
  exit 1
}
$fragment = Get-Content $fragmentPath -Raw | ConvertFrom-Json
$hashes = $fragment.'01-author-cost-cap-tests'.testFileHashes
if (-not $hashes -or ($hashes | Get-Member -MemberType NoteProperty).Count -eq 0) {
  Write-Output "state key '01-author-cost-cap-tests.testFileHashes' is missing or empty"
  exit 1
}
exit 0
