# catches: the action claimed to create the greeting script but never wrote the file
if (Test-Path "out/greet.ps1") { exit 0 }
Write-Output "out/greet.ps1 does not exist in the workspace"
exit 1
