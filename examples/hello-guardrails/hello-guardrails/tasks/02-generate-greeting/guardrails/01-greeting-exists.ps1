# catches: the agent claimed success but never wrote the greeting file
if (Test-Path "out/greeting.txt") { exit 0 }
Write-Output "out/greeting.txt does not exist in the workspace"
exit 1
