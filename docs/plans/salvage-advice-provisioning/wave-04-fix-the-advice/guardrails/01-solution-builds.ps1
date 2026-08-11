# catches: the fully-merged plan HEAD not compiling - individual waves passed in isolation but the
#          union of all four does not build.
$log = dotnet build Guardrails.sln -c Release 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    foreach ($line in ($log -split "`r?`n")) { if ($line -match ': error ') { Write-Output $line } }
    Write-Output "the merged plan HEAD does not build"
    exit 1
}
exit 0
