# catches: the seed step reporting success without writing the file, or writing something
#          other than the recipient the plan dictated
if (-not (Test-Path 'out/recipient.txt')) {
    Write-Output 'out/recipient.txt does not exist in the workspace'
    exit 1
}
if ((Get-Content 'out/recipient.txt' -Raw).Trim() -ne 'World') {
    Write-Output "out/recipient.txt does not name the recipient the plan dictated ('World')"
    exit 1
}
exit 0
