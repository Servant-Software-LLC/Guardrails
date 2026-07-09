# catches: the action claimed to generate the greeting but never wrote out/greeting.txt, or wrote it
#          empty.
if (-not (Test-Path "out/greeting.txt")) {
    Write-Output "out/greeting.txt does not exist in the workspace"
    exit 1
}
if ([string]::IsNullOrWhiteSpace((Get-Content -Raw -Path "out/greeting.txt"))) {
    Write-Output "out/greeting.txt is empty — no greeting was written"
    exit 1
}
exit 0
