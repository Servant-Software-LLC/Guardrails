# catches: greeting file exists but greets the wrong recipient (didn't read state)
#          or has the wrong shape (didn't actually run the script)
$state = Get-Content $env:GUARDRAILS_STATE_IN -Raw | ConvertFrom-Json
$expected = $state.recipientName
if ([string]::IsNullOrWhiteSpace($expected)) {
    Write-Output "state snapshot has no recipientName — seed.json missing or merge broken"
    exit 1
}
$content = (Get-Content "out/greeting.txt" -Raw).Trim()
if ($content -ne "Hello, $expected!") {
    Write-Output "out/greeting.txt contains '$content' but state.recipientName='$expected' requires 'Hello, $expected!'"
    exit 1
}
exit 0
