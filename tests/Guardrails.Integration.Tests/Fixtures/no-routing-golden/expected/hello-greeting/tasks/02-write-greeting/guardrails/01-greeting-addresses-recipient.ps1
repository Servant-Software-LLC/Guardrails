# catches: a greeting file that exists but greets nobody, or greets someone the seed step
#          never named (the agent inventing a recipient instead of reading one)
if (-not (Test-Path 'out/greeting.txt')) {
    Write-Output 'out/greeting.txt does not exist in the workspace'
    exit 1
}
$recipient = (Get-Content 'out/recipient.txt' -Raw).Trim()
$greeting = (Get-Content 'out/greeting.txt' -Raw).Trim()
if ($greeting -ne "Hello, $recipient!") {
    Write-Output "out/greeting.txt is '$greeting'; expected 'Hello, $recipient!'"
    exit 1
}
exit 0
