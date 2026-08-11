# catches: the fully-merged plan HEAD regressing the suite - the salvage-advice rewrite, the scanner
#          change and the grant injection each passed alone but break something together.
$log = dotnet test Guardrails.sln -c Release 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "---- failure detail (why) ----"
    foreach ($line in ($log -split "`r?`n")) { if ($line -match 'error|Assert\.|Exception|\[FAIL\]|at Guardrails') { Write-Output $line } }
    Write-Output "the full suite fails on the merged plan HEAD"
    exit 1
}
exit 0
