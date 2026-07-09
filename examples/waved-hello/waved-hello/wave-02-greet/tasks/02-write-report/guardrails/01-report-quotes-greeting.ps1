# catches: a report that exists but does not quote the ACTUAL generated greeting — a hollow report
#          that narrates success without embedding the real out/greeting.txt content.
if (-not (Test-Path "out/report.md")) {
    Write-Output "out/report.md does not exist in the workspace"
    exit 1
}
$greeting = (Get-Content -Raw -Path "out/greeting.txt").Trim()
$report = Get-Content -Raw -Path "out/report.md"
if ($report -notmatch [regex]::Escape($greeting)) {
    Write-Output "out/report.md does not quote the generated greeting ('$greeting')"
    exit 1
}
exit 0
